import {createServer} from 'node:http';
import {readFile, stat} from 'node:fs/promises';
import {resolve, sep, extname} from 'node:path';
import assert from 'node:assert/strict';
import {chromium} from 'playwright';

const root = resolve('build');
const mime = {'.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.json': 'application/json', '.png': 'image/png', '.woff2': 'font/woff2'};
const server = createServer(async (request, response) => {
  try {
    let path = resolve(root, `.${decodeURIComponent(new URL(request.url, 'http://localhost').pathname).replaceAll('/', sep)}`);
    if (path !== root && !path.startsWith(root + sep)) { response.writeHead(403).end(); return; }
    if ((await stat(path)).isDirectory()) path = resolve(path, 'index.html');
    response.setHeader('Content-Type', mime[extname(path)] ?? 'application/octet-stream');
    response.end(await readFile(path));
  } catch { response.writeHead(404).end(); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const address = server.address();
const base = `http://127.0.0.1:${address.port}`;
let browser;
const repository = 'https://github.com/homotechsual/dotMARC';
const roadmap = {
  updatedAt: '2026-11-22T18:00:00Z', stale: false, submissionsEnabled: true,
  issues: [
    {number: 1, title: 'Bulk DNS push', url: `${repository}/issues/1`, status: 'planned', milestone: 1, completed: false, awaitingRelease: false, statusWarning: false, changeType: 'enhancement', areas: ['dns', 'domains'], classificationWarning: false},
    {number: 2, title: 'Action-centre Dashboard', url: `${repository}/issues/2`, status: 'in-progress', milestone: 1, completed: true, awaitingRelease: true, statusWarning: false, changeType: 'feature', areas: ['domains', 'dmarc', 'alerts'], classificationWarning: false},
    {number: 3, title: 'An unscheduled idea', url: `${repository}/issues/3`, status: 'considering', milestone: null, completed: false, awaitingRelease: false, statusWarning: false},
    {number: 4, title: 'An older shipped feature', url: `${repository}/issues/4`, status: 'shipped', milestone: 2, completed: true, awaitingRelease: false, statusWarning: false},
  ],
  milestones: [
    {number: 1, title: 'v0.14.0', description: 'Bulk push and action centre', url: `${repository}/milestone/1`, dueOn: null, released: false, releaseUrl: null},
    {number: 2, title: 'v0.13.0', description: 'Previously shipped', url: `${repository}/milestone/2`, dueOn: null, released: true, releaseUrl: `${repository}/releases/tag/v0.13.0`},
  ],
};
try {
  browser = await chromium.launch({headless: true});
  const page = await browser.newPage();
  const pageErrors = [];
  page.on('pageerror', error => pageErrors.push(error.message));
  await page.route('https://feedback-api.dotmarc.app/**', async route => {
    if (route.request().method() === 'OPTIONS') {
      await route.fulfill({status: 204, headers: {'Access-Control-Allow-Origin': base, 'Access-Control-Allow-Methods': 'GET, POST, OPTIONS', 'Access-Control-Allow-Headers': 'Content-Type'}});
    } else if (route.request().url().endsWith('/requests')) {
      const body = route.request().postDataJSON();
      assert.equal(body.publicConsent, true);
      assert.equal(body.turnstileToken, 'browser-test-token');
      assert.equal(body.changeType, 'bug');
      assert.deepEqual(body.areas, ['dns', 'spf-dkim']);
      assert.match(body.requestId, /^[0-9a-f-]{36}$/);
      await route.fulfill({status: 201, json: {issue: {number: 42, url: `${repository}/issues/42`}}, headers: {'Access-Control-Allow-Origin': base}});
    } else {
      await route.fulfill({json: roadmap, headers: {'Access-Control-Allow-Origin': base}});
    }
  });
  await page.route('https://challenges.cloudflare.com/turnstile/**', route => route.fulfill({
    contentType: 'application/javascript',
    body: 'window.turnstile = { render: (el, options) => { setTimeout(() => options.callback("browser-test-token"), 0); return "test"; }, reset: () => {}, remove: () => {} };',
  }));
  for (const route of ['/feedback/', '/roadmap/']) {
    await page.goto(`${base}${route}`);
    await page.getByRole('heading', {name: 'v0.14.0', exact: true}).waitFor();
    assert.equal(await page.getByRole('button', {name: 'Planned releases', exact: true}).getAttribute('aria-pressed'), 'true');
    assert.equal(await page.getByRole('button', {name: 'Board', exact: true}).getAttribute('aria-pressed'), 'false');
  }
  await page.getByRole('heading', {name: 'v0.14.0', exact: true}).waitFor();
  assert.equal(await page.getByRole('link', {name: 'An older shipped feature', exact: true}).isVisible(), false);
  await page.getByText('Previously shipped releases (1)', {exact: true}).click();
  assert.equal(await page.getByRole('link', {name: 'An older shipped feature', exact: true}).isVisible(), true);
  for (const width of [1024, 1100, 1280]) {
    await page.setViewportSize({width, height: 900});
    const links = page.locator('.navbar__items--left > .navbar__link');
    for (const link of await links.all()) {
      assert.ok(await link.evaluate(element => element.getBoundingClientRect().height < 45), `Navbar link wraps at ${width}px`);
    }
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true);
  }
  await page.getByRole('button', {name: 'Resources', exact: true}).hover();
  await page.getByRole('link', {name: 'API reference', exact: true}).waitFor();
  assert.equal(await page.getByRole('link', {name: 'Submit directly on GitHub'}).getAttribute('href'), `${repository}/issues/new?template=feature-request.yml`);
  await page.getByRole('button', {name: 'Search', exact: true}).click();
  const searchDialog = page.getByRole('dialog', {name: 'Search the site'});
  await searchDialog.waitFor();
  await searchDialog.getByPlaceholder('Search documentation, API and updates...', {exact: true}).fill('getting started');
  await searchDialog.locator('button.docsense-hit').filter({hasText: /Getting Started/i}).first().click();
  await page.waitForURL('**/docs/getting-started/**');
  await page.goto(`${base}/roadmap/`);
  await page.getByRole('heading', {name: 'v0.14.0', exact: true}).waitFor();
  assert.equal(await page.getByText('No release date committed.').count(), 1);
  assert.equal(await page.getByText('1 of 2 public items completed.', {exact: false}).count(), 1);
  await page.getByLabel('Product area', {exact: true}).selectOption('dns');
  await page.getByLabel('Roadmap filters').getByLabel('Change type', {exact: true}).selectOption('enhancement');
  assert.equal(await page.getByRole('link', {name: 'Bulk DNS push', exact: true}).count(), 1);
  assert.equal(await page.getByRole('link', {name: 'Action-centre Dashboard', exact: true}).count(), 0);
  assert.equal(await page.getByText('1 of 2 public items completed.', {exact: false}).count(), 1);
  await page.getByRole('button', {name: 'Board', exact: true}).click();
  await page.getByRole('heading', {name: 'Planned (1)', exact: true}).waitFor();
  assert.equal(await page.getByRole('link', {name: 'Action-centre Dashboard', exact: true}).count(), 0);
  await page.getByRole('button', {name: 'Clear filters', exact: true}).click();
  await page.getByRole('button', {name: 'Board', exact: true}).click();
  assert.equal(await page.getByRole('heading', {name: 'Shipped (1)', exact: true}).count(), 0);
  await page.getByLabel('Include shipped items').check();
  await page.getByRole('heading', {name: 'Shipped (1)', exact: true}).waitFor();
  await page.getByLabel('Include shipped items').uncheck();
  await page.getByRole('heading', {name: 'Planned (1)', exact: true}).waitFor();
  await page.getByRole('searchbox', {name: 'Find an item'}).fill('Bulk DNS');
  assert.equal(await page.getByRole('link', {name: 'Bulk DNS push', exact: true}).count(), 1);
  assert.equal(await page.getByRole('link', {name: 'An unscheduled idea', exact: true}).count(), 0);
  await page.getByRole('searchbox', {name: 'Find an item'}).fill('');
  await page.getByRole('button', {name: 'Planned releases', exact: true}).click();
  await page.getByRole('searchbox', {name: 'Find an item'}).fill('Bulk DNS');
  assert.equal(await page.getByText('1 of 2 public items completed.', {exact: false}).count(), 1);
  await page.getByRole('searchbox', {name: 'Find an item'}).fill('');
  for (const theme of ['light', 'dark']) {
    await page.evaluate(theme => document.documentElement.setAttribute('data-theme', theme), theme);
    for (const width of [390, 768, 1280]) {
      await page.setViewportSize({width, height: 900});
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true, `${theme} overflow at ${width}px`);
      const card = page.getByRole('article').filter({has: page.getByRole('link', {name: 'Bulk DNS push', exact: true})});
      const size = await card.boundingBox();
      assert.ok(size && size.height < 210, `Two-area card should stay compact at ${width}px in ${theme}`);
      const meta = card.getByText('Enhancement', {exact: true});
      assert.ok(Number.parseFloat(await meta.evaluate(element => getComputedStyle(element).fontSize)) <= 12, 'Metadata must remain secondary to the title');
      assert.equal(await card.getByRole('list', {name: 'Product areas'}).getByRole('listitem').count(), 2);
      if (process.env.ROADMAP_SCREENSHOT_DIR && width !== 768) {
        await page.screenshot({path: resolve(process.env.ROADMAP_SCREENSHOT_DIR, `roadmap-${theme}-${width}.png`), fullPage: true});
      }
    }
  }
  await page.setViewportSize({width: 390, height: 844});
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true);
  await page.getByRole('button', {name: 'Search', exact: true}).click();
  await page.getByRole('dialog', {name: 'Search the site'}).waitFor();
  await page.keyboard.press('Escape');
  await page.getByRole('dialog', {name: 'Search the site'}).waitFor({state: 'hidden'});
  await page.getByRole('link', {name: 'Submit feedback', exact: true}).click();
  await page.getByLabel('Short title').fill('A helpful browser-tested feature');
  await page.getByLabel('Change type', {exact: true}).last().selectOption('bug');
  await page.getByLabel('DNS & provider push', {exact: true}).check();
  await page.getByLabel('SPF & DKIM', {exact: true}).check();
  await page.getByLabel('What would you like to change?').fill('Please add a useful new capability for our team.');
  await page.getByLabel('What would this help you do?').fill('This would simplify onboarding our client domains.');
  await page.getByLabel('I understand this request will be public.').check();
  await page.getByRole('button', {name: 'Submit request', exact: true}).click();
  await page.getByRole('link', {name: '#42 on GitHub'}).waitFor();
  assert.equal(await page.getByRole('link', {name: '#42 on GitHub'}).getAttribute('href'), `${repository}/issues/42`);
  roadmap.stale = true;
  await page.reload();
  await page.getByText('Showing cached data; GitHub refresh is unavailable.', {exact: false}).waitFor();
  await page.route('https://feedback-api.dotmarc.app/roadmap', route => route.fulfill({status: 503, json: {error: 'Unavailable'}, headers: {'Access-Control-Allow-Origin': base}}));
  await page.reload();
  await page.getByRole('button', {name: 'Retry', exact: true}).waitFor();
  assert.ok(await page.getByRole('link', {name: 'Submit directly on GitHub'}).isVisible());
  assert.deepEqual(pageErrors, []);
  console.log('Website browser checks passed: DocSense search/results, release/board views, filtering, mobile layout, form/receipt, stale and error states.');
} finally {
  if (browser) await browser.close();
  await new Promise(resolve => server.close(resolve));
}
