// The way through InvoiceMesh in a real browser (the installed Edge, headless) against a running stack:
// setup or sign-in, sample data, waiting for the processing, then every main page and every tab of every document, desktop or phone.
// Fails on a page that does not answer 200, on a script error in the console and when the sample documents are not what they should be.
//   cd tests/e2e && npm install && node flow.js http://localhost:2308            (SHOTS=<folder> keeps screenshots, MOBILE=1 for 390 px)
// The administrator is admin@example.com / Start-12345-Mesh (created on a fresh stack).
const { chromium } = require('playwright-core');
const fs = require('fs');

const base = process.argv[2] || 'http://localhost:2308';
const shots = process.env.SHOTS;
const mobile = process.env.MOBILE === '1';
const email = 'admin@example.com';
const password = 'Start-12345-Mesh';

function fail(message) {
  console.error('FAIL ' + message);
  process.exitCode = 1;
}

(async () => {
  if (shots) fs.mkdirSync(shots, { recursive: true });
  const browser = await chromium.launch({ channel: process.env.BROWSER || 'msedge', headless: true });
  const context = await browser.newContext(mobile
    ? { viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true }
    : { viewport: { width: 1440, height: 1000 } });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', e => errors.push(`${page.url()}: ${e.message}`));
  page.on('console', m => { if (m.type() === 'error') errors.push(`${page.url()}: ${m.text()}`); });

  // first start: the setup assistant; afterwards the sign-in
  await page.goto(base + '/', { waitUntil: 'networkidle' });
  if (page.url().includes('/Account/Setup')) {
    await page.fill('#Input_DisplayName', 'Anna Müller');
    await page.fill('#Input_Email', email);
    await page.fill('#Input_Password', password);
    await page.fill('#Input_PasswordRepeat', password);
    await Promise.all([page.waitForNavigation(), page.click('button[type=submit]')]);
  }
  if (page.url().includes('/Account/Login')) {
    await page.fill('#Input_Email', email);
    await page.fill('#Input_Password', password);
    await Promise.all([page.waitForNavigation(), page.click('button[type=submit]')]);
  }

  // the sample documents, once (through the real button and its confirmation)
  await page.goto(base + '/Admin/System', { waitUntil: 'networkidle' });
  const demo = await page.$('form button:has-text("Beispieldaten anlegen")');
  if (demo) {
    await demo.click();
    const ok = await page.waitForSelector('dialog[open] button:has-text("Anlegen")', { timeout: 5000 }).catch(() => null);
    if (ok) await Promise.all([page.waitForNavigation(), ok.click()]);
  }

  // the processing runs in the background: wait until nothing waits any more
  await page.goto(base + '/Invoices', { waitUntil: 'networkidle' });
  let ids = await page.$$eval('a[href^="/Invoices/Details?id="]', as => [...new Set(as.map(a => a.getAttribute('href').split('=')[1]))]);
  for (let i = 0; i < 90; i++) {
    const r = await page.request.get(`${base}/api/invoices/status?ids=${ids.join(',')}`);
    if ((await r.json()).pending === 0) break;
    await page.waitForTimeout(2000);
  }

  // what the samples must have become
  await page.goto(base + '/Invoices', { waitUntil: 'networkidle' });
  const list = await page.textContent('main');
  for (const expected of ['RE-2026-0815', 'FA-2026-117', 'CN-9001', '2026-10477', 'BD-3391', 'Notizen.docx', 'Fehlgeschlagen', 'Ungültig']) {
    if (!list.includes(expected)) fail(`the list of documents lacks "${expected}"`);
  }

  const pages = [
    ['dashboard', '/'], ['invoices', '/Invoices'], ['upload', '/Invoices/Upload'], ['schemas', '/Schemas'], ['schema', '/Schemas/Edit?id=1'],
    ['mappings', '/Mappings'], ['mapping', '/Mappings/Edit?id=1'], ['rule', '/Mappings/Rule?id=1&target=seller.name'], ['preview', '/Mappings/Test?id=1'],
    ['exports', '/Exports'], ['export', '/Exports/Edit?id=3'], ['formats', '/Formats'], ['processing', '/Admin/Processing'], ['backup', '/Admin/Backup'],
  ];
  for (const id of ids) {
    for (const tab of ['Details', 'Original', 'Data', 'Validation', 'Files', 'History', 'Correct', 'Share']) pages.push([`doc${id}-${tab.toLowerCase()}`, `/Invoices/${tab}?id=${id}`]);
  }

  for (const [name, url] of pages) {
    const response = await page.goto(base + url, { waitUntil: 'networkidle' });
    if (!response || response.status() !== 200) fail(`${url} answered ${response && response.status()}`);
    if (shots) {
      // the shell scrolls inside .mat-main: the viewport is made as tall as the content for a picture of the whole page
      const size = page.viewportSize();
      const height = await page.evaluate(() => Math.max(document.querySelector('.mat-main')?.scrollHeight || 0, document.documentElement.scrollHeight));
      await page.setViewportSize({ width: size.width, height: Math.min(Math.max(height, size.height), 7000) });
      await page.screenshot({ path: `${shots}/${name}.png` });
      await page.setViewportSize(size);
    }
  }


  for (const e of errors) fail('console: ' + e);
  console.log(process.exitCode ? 'flow failed' : `flow passed: ${pages.length} pages, ${ids.length} documents`);
  await browser.close();
})().catch(e => { console.error(e); process.exit(1); });
