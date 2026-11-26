import assert from 'node:assert/strict';
import {chromium} from 'playwright';

const base = process.env.INVENTORY_TEST_URL ?? 'http://127.0.0.1:5187';
const browser = await chromium.launch({channel: 'msedge', headless: true});
const errors = [];
try {
  const page = await browser.newPage({viewport: {width: 1440, height: 1100}});
  page.on('pageerror', error => errors.push(error.message));
  await page.request.post(`${base}/demo/sign-in/admin`);
  const enter = async path => {
    await page.goto(`${base}${path}`);
    await page.waitForFunction(() => !!JSON.parse(history.state?.userState ?? '{}').domainListEntry);
    await page.locator('.dashboard-metric').first().waitFor();
  };
  await enter('/dashboard');
  assert.equal(await page.locator('.dashboard-metric').count(), 8);
  assert.equal(await page.locator('.domain-inventory').count(), 0);
  assert.equal(await page.getByRole('button', {name: /^Filters \(/}).count(), 1);
  const dimensions = await page.locator('.dashboard-metric').evaluateAll(cards => cards.map(card => ({
    height: card.getBoundingClientRect().height, top: card.getBoundingClientRect().top
  })));
  assert(dimensions.slice(0, 4).every(card => Math.abs(card.height - dimensions[0].height) < 2));
  assert(dimensions.slice(0, 4).every(card => Math.abs(card.top - dimensions[0].top) < 2));
  assert(await page.locator('.dashboard-operations').isVisible());
  await page.getByRole('button', {name: /^Sender decisions outstanding:/}).click();
  await page.getByText(/Outstanding sender decisions from the last 30 days · \d+ domains/).waitFor();
  assert.equal(new URL(page.url()).searchParams.get('attention'), 'senders');
  await page.getByRole('button', {name: 'Show all attention', exact: true}).click();
  const categories = page.getByRole('group', {name: 'Issue categories'});
  await categories.getByRole('button', {name: /^DNS\/configuration/}).click();
  await categories.getByRole('button', {name: /^Missing reports/}).click();
  await page.waitForURL(target => target.searchParams.get('attention') === 'dns,missing');
  assert.equal(await categories.getByRole('button', {name: /^DNS\/configuration/}).getAttribute('aria-pressed'), 'true');
  assert.equal(await categories.getByRole('button', {name: /^Missing reports/}).getAttribute('aria-pressed'), 'true');
  await categories.getByRole('button', {name: /^All \(/}).click();
  await page.waitForURL(target => !target.searchParams.has('attention'));

  const investigation = page.locator('.dashboard-investigations');
  const firstRow = investigation.locator('tbody tr').first();
  const quarantined = await firstRow.locator('[data-quarantined]').innerText();
  const rejected = await firstRow.locator('[data-rejected]').innerText();
  await firstRow.getByRole('button', {name: /^View seven-day records/}).click();
  const dialog = page.getByRole('dialog');
  await dialog.waitFor();
  assert((await dialog.innerText()).includes(`${quarantined} quarantined and ${rejected} rejected messages`));
  assert(await dialog.getByText('SPF:', {exact: false}).count() > 0);
  await dialog.getByRole('button', {name: 'Close', exact: true}).click();
  await dialog.waitFor({state: 'hidden'});
  await page.evaluate(() => scrollTo({top: 0, behavior: 'instant'}));
  await page.waitForFunction(() => scrollY === 0);
  if (process.env.DASHBOARD_SCREENSHOT) await page.screenshot({path: process.env.DASHBOARD_SCREENSHOT, fullPage: true});
  await page.getByTitle('Toggle dark mode', {exact: true}).click();
  await page.waitForFunction(() => localStorage.getItem('dotmarc-dark-mode') === 'true');
  if (process.env.DASHBOARD_DARK_SCREENSHOT) await page.screenshot({path: process.env.DASHBOARD_DARK_SCREENSHOT, fullPage: true});

  await page.getByRole('button', {name: /^Filters \(/}).click();
  await page.getByLabel('Domain search', {exact: true}).fill('aurora');
  await page.waitForURL(target => target.searchParams.get('search') === 'aurora');
  await page.waitForFunction(() => [...document.querySelectorAll('a[data-domain-link]')].every(link => new URL(link.href).pathname.split('/')[2].includes('aurora')));
  await page.getByRole('button', {name: /^Sender decisions outstanding:/}).click();
  await page.waitForURL(target => target.searchParams.get('attention') === 'senders');
  const before = page.url();
  await page.locator('.dashboard-attention a[data-domain-link]').first().click();
  await page.waitForURL(target => target.pathname.startsWith('/domains/'));
  await page.getByRole('button', {name: 'Back', exact: true}).waitFor({state: 'visible'});
  await page.getByRole('button', {name: 'Back', exact: true}).click();
  await page.waitForURL(before);
  assert.equal(await page.getByLabel('Domain search', {exact: true}).inputValue(), 'aurora');
  await page.getByText(/Outstanding sender decisions from the last 30 days · \d+ domains/).waitFor();
  assert(await page.locator('.dashboard-attention .dashboard-domain-name').count() > 0);
  assert(await page.locator('.dashboard-attention .dashboard-issue-kind').count() > 0);

  await page.setViewportSize({width: 390, height: 844});
  await enter('/dashboard');
  assert(await page.locator('.dashboard-metric .domain-percentage').evaluateAll(nodes =>
    nodes.every(node => getComputedStyle(node).whiteSpace === 'nowrap' && node.scrollWidth <= node.clientWidth + 1)));
  assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
  assert(await page.locator('.dashboard-attention .dashboard-problem').evaluateAll(nodes =>
    nodes.filter(node => node.checkVisibility()).every(node => node.getBoundingClientRect().width > 250)));
  if (process.env.DASHBOARD_MOBILE_SCREENSHOT) await page.screenshot({path: process.env.DASHBOARD_MOBILE_SCREENSHOT, fullPage: true});

  await enter('/dashboard?search=not-a-domain');
  await page.getByText('No domains in the current scope.', {exact: false}).waitFor();
  assert(await page.getByText('No message data is available', {exact: false}).isVisible());
  assert.equal(new URL(page.url()).pathname, '/dashboard');
  assert.equal(new URL(page.url()).searchParams.get('search'), 'not-a-domain');
  const domainInput = page.getByLabel('Domain name', {exact: true});
  await domainInput.fill('not a valid domain');
  await page.getByRole('button', {name: 'Add domain', exact: true}).click();
  await page.getByText('Enter a valid domain name.', {exact: true}).waitFor();
  const addedDomain = `not-a-domain-${Date.now()}.example`;
  await domainInput.fill(addedDomain);
  await page.getByRole('button', {name: 'Add domain', exact: true}).click();
  await page.getByText('Domain added. Active filters may hide it.', {exact: true}).waitFor();
  await page.locator('a[data-domain-link]').filter({hasText: addedDomain}).waitFor();
  await domainInput.fill(addedDomain);
  await page.getByRole('button', {name: 'Add domain', exact: true}).click();
  await page.getByText('That domain already exists.', {exact: true}).waitFor();
  const viewer = await browser.newPage();
  await viewer.request.post(`${base}/demo/sign-in/viewer`);
  await viewer.goto(`${base}/dashboard?group=all`);
  await viewer.waitForFunction(() => !!JSON.parse(history.state?.userState ?? '{}').domainListEntry);
  await viewer.locator('.dashboard-metric').first().waitFor();
  await viewer.locator('a[data-domain-link]').first().waitFor();
  const names = await viewer.locator('a[data-domain-link]').allTextContents();
  assert(names.length > 0 && names.every(name => !name.includes('cobalt-freight')));
  assert.equal(await viewer.locator('.dashboard-operations').count(), 0);
  await viewer.close();
  assert.deepEqual(errors, []);
  console.log('Dashboard browser checks passed: cards, multi-category queue, hierarchy, evidence, Back, mobile, inline domain validation/creation and scoped Viewer.');
} finally {
  await browser.close();
}
