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
    {number: 1, title: 'Bulk DNS push', url: `${repository}/issues/1`, status: 'planned', milestone: 1, completed: false, awaitingRelease: false, statusWarning: false},
    {number: 2, title: 'Action-centre Dashboard', url: `${repository}/issues/2`, status: 'in-progress', milestone: 1, completed: true, awaitingRelease: true, statusWarning: false},
    {number: 3, title: 'An unscheduled idea', url: `${repository}/issues/3`, status: 'considering', milestone: null, completed: false, awaitingRelease: false, statusWarning: false},
  ],
  milestones: [{number: 1, title: 'v0.14.0', description: 'Bulk push and action centre', url: `${repository}/milestone/1`, dueOn: null, released: false, releaseUrl: null}],
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
  await page.goto(`${base}/roadmap/`);
  await page.getByRole('heading', {name: 'v0.14.0', exact: true}).waitFor();
  await page.getByRole('button', {name: 'Search', exact: true}).click();
  const searchDialog = page.getByRole('dialog', {name: 'Search docs'});
  await searchDialog.waitFor();
  await searchDialog.getByPlaceholder('Search...', {exact: true}).fill('getting started');
  await searchDialog.locator('button.docsense-hit').filter({hasText: /Getting Started/i}).first().click();
  await page.waitForURL('**/docs/getting-started/**');
  await page.goto(`${base}/roadmap/`);
  await page.getByRole('heading', {name: 'v0.14.0', exact: true}).waitFor();
  assert.equal(await page.getByText('No release date committed.').count(), 1);
  assert.equal(await page.getByText('1 of 2 public features completed.', {exact: false}).count(), 1);
  await page.getByRole('button', {name: 'Board', exact: true}).click();
  await page.getByRole('heading', {name: 'Planned (1)', exact: true}).waitFor();
  await page.getByRole('searchbox', {name: 'Find a feature'}).fill('Bulk DNS');
  assert.equal(await page.getByRole('link', {name: 'Bulk DNS push', exact: true}).count(), 1);
  assert.equal(await page.getByRole('link', {name: 'An unscheduled idea', exact: true}).count(), 0);
  await page.getByRole('searchbox', {name: 'Find a feature'}).fill('');
  await page.setViewportSize({width: 390, height: 844});
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true);
  await page.getByLabel('Short title').fill('A helpful browser-tested feature');
  await page.getByLabel('What would you like to change?').fill('Please add a useful new capability for our team.');
  await page.getByLabel('What would this help you do?').fill('This would simplify onboarding our client domains.');
  await page.getByLabel('I understand this request will be public.').check();
  await page.getByRole('button', {name: 'Submit feature request', exact: true}).click();
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
