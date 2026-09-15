// Shared trusted browser actions for execution and promoted regressions.
const Ajv = require('ajv/dist/2020');
const schema = require('../../schemas/v1/execution-manifest.schema.json');
const validate = new Ajv({ strict: true }).compile(schema);
function register(manifest, allowedOrigins, test, expect) {
  if (!validate(manifest)) throw new Error('Invalid regression manifest');
  const origins = new Set(allowedOrigins.map(value => {
    const url = new URL(value);
    if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.pathname !== '/' || url.search || url.hash)
      throw new Error('Allowlist entries must be exact HTTP(S) origins');
    return url.origin;
  }));
  const target = new URL(manifest.target);
  if (target.username || target.password || !origins.has(target.origin)) throw new Error('Regression target is not allowlisted');
  const ids = new Set();
  for (const item of manifest.tests) {
    if (ids.has(item.testCaseId)) throw new Error('Duplicate regression test');
    ids.add(item.testCaseId);
    if (!item.steps.some(step => step.action.startsWith('expect'))) throw new Error('Regression requires an assertion');
    for (const step of item.steps) {
      if (['goto', 'expectUrl'].includes(step.action)) {
        const url = new URL(step.value, manifest.target);
        if (!origins.has(url.origin) || url.username || url.password) throw new Error('Regression navigation is not allowlisted');
      }
    }
  }
for (const entry of manifest.tests) {
  test(entry.testCaseId, async ({ page, context }) => {
    const blocked = [];
    await context.routeWebSocket('**/*', socket => { blocked.push('websocket'); socket.close(); });
    await context.route('**/*', async route => {
      const url = new URL(route.request().url());
      if (!origins.has(url.origin) || !['http:', 'https:'].includes(url.protocol)) {
        blocked.push(url.origin); await route.abort('blockedbyclient'); return;
      }
      try {
        // Do not let redirects bypass interception. Redirect-dependent flows need a later explicit policy.
        const response = await route.fetch({ maxRedirects: 0, timeout: 10000 });
        if (response.status() >= 300 && response.status() < 400 && response.status() !== 304) {
          blocked.push('redirect'); await route.abort('blockedbyclient');
        } else await route.fulfill({ response });
      } catch { blocked.push('request-failed'); await route.abort('failed').catch(() => {}); }
    });
    for (const step of entry.steps) {
      try {
      switch (step.action) {
        case 'goto': await page.goto(new URL(step.value, manifest.target).href); break;
        case 'click': await page.locator(step.selector).click(); break;
        case 'fill': await page.locator(step.selector).fill(step.value); break;
        case 'expectText': await expect(page.locator(step.selector)).toHaveText(step.value); break;
        case 'expectVisible': await expect(page.locator(step.selector)).toBeVisible(); break;
        case 'expectUrl': await expect(page).toHaveURL(new URL(step.value, manifest.target).href); break;
        default: throw new Error('Unsupported manifest action');
      }
      } catch (error) {
        const kind = step.action.startsWith('expect') ? 'QUALITY_ASSERTION_FAILURE' : 'QUALITY_TEST_FAILURE';
        test.info().annotations.push({ type: 'quality-failure', description: blocked.length ? 'InfrastructureFailure' : step.action.startsWith('expect') ? 'NeedsReview' : 'TestFailure' });
        throw new Error(kind + ': ' + error.message);
      }
    }
    if (blocked.length) throw new Error('QUALITY_INFRASTRUCTURE_FAILURE: Blocked or failed network requests');
  });
}
}
module.exports = { register };
