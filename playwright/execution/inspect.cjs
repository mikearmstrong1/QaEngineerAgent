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
      const capabilitiesFor = node => {
        const tag = node.tagName.toLowerCase(), type = (node.getAttribute('type') || '').toLowerCase();
        const capabilities = [];
        if (!node.disabled && (tag === 'button' || tag === 'a' || node.getAttribute('role') === 'button' || type === 'checkbox')) capabilities.push('click');
        if (!node.disabled && !node.readOnly && (tag === 'textarea' || (tag === 'input' && !['button','submit','checkbox','radio','hidden','file'].includes(type)))) capabilities.push('fill');
        return capabilities;
      };
      const safeHrefFor = node => {
        if (node.tagName.toLowerCase() !== 'a' || !node.href) return null;
        const url = new URL(node.href, document.baseURI);
        url.username = ''; url.password = ''; url.search = ''; url.hash = '';
        return url.origin === location.origin ? `${url.pathname}` : `${url.origin}${url.pathname}`;
      };
      const visible = nodes.filter(node => { const style = getComputedStyle(node); return style.display !== 'none' && style.visibility !== 'hidden'; }).slice(0, 100);
      return visible.map(node => {
        const selector = selectorFor(node), id = node.getAttribute('id');
        const explicitLabel = id ? document.querySelector(`label[for="${CSS.escape(id)}"]`)?.textContent : null;
        const wrappingLabel = node.closest('label')?.textContent;
        const formLabel = (explicitLabel || wrappingLabel || '').trim().slice(0, 500) || null;
        const accessibleName = (node.getAttribute('aria-label') || formLabel || node.getAttribute('placeholder') || node.textContent || '').trim().slice(0, 500);
        return {
          tag: node.tagName.toLowerCase(), role: node.getAttribute('role') || (node.tagName.toLowerCase() === 'a' ? 'link' : node.tagName.toLowerCase()),
          label: accessibleName, selector, capabilities: capabilitiesFor(node), testId: node.dataset.testid || null,
          formLabel, href: safeHrefFor(node),
          unique: document.querySelectorAll(selector).length === 1, disabled: Boolean(node.disabled), readOnly: Boolean(node.readOnly)
        };
      });
    });
    process.stdout.write(JSON.stringify({ target, controls }));
  } finally { await browser.close(); }
})().catch(() => process.exitCode = 1);
