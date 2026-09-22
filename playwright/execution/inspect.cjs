// Trusted read-only UI inventory. It never runs caller-provided code or records form values.
const { chromium } = require('playwright');
const [target, originsText] = process.argv.slice(2);
const origins = new Set((originsText || '').split(';').filter(Boolean));
const originFor = value => new URL(value).origin;
(async () => {
  if (!origins.has(originFor(target))) throw new Error('target_not_allowlisted');
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext();
    await context.route('**/*', route => origins.has(originFor(route.request().url())) ? route.continue() : route.abort());
    const page = await context.newPage();
    await page.goto(target, { waitUntil: 'domcontentloaded', timeout: 20000 });
    const controls = await page.locator('h1,button,input,select,textarea,a,[role="button"],[role="link"],[role="checkbox"],[role="textbox"]').evaluateAll(nodes => {
      const selectorFor = element => {
        const escape = value => CSS.escape(value);
        if (element.dataset.testid) return `[data-testid="${escape(element.dataset.testid)}"]`;
        if (element.id) return `#${escape(element.id)}`;
        if (element.getAttribute('aria-label')) return `${element.tagName.toLowerCase()}[aria-label="${escape(element.getAttribute('aria-label'))}"]`;
        if (element.getAttribute('name')) return `${element.tagName.toLowerCase()}[name="${escape(element.getAttribute('name'))}"]`;
        if (element.tagName.toLowerCase() === 'h1') return 'h1';
        return element.tagName.toLowerCase();
      };
      return nodes.filter(node => { const style = getComputedStyle(node); return style.display !== 'none' && style.visibility !== 'hidden'; })
        .slice(0, 100).map(node => ({
          tag: node.tagName.toLowerCase(), role: node.getAttribute('role') || (node.tagName.toLowerCase() === 'a' ? 'link' : node.tagName.toLowerCase()),
          label: (node.getAttribute('aria-label') || node.getAttribute('placeholder') || node.textContent || '').trim().slice(0, 500), selector: selectorFor(node)
        }));
    });
    process.stdout.write(JSON.stringify({ target, controls }));
  } finally { await browser.close(); }
})().catch(() => process.exitCode = 1);
