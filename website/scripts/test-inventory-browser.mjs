import assert from 'node:assert/strict';
import {chromium} from 'playwright';

const base = process.env.INVENTORY_TEST_URL ?? 'http://127.0.0.1:5187';
const browser = await chromium.launch({channel: 'msedge', headless: true});
const errors = [];
try {
  const page = await browser.newPage({viewport: {width: 1440, height: 900}});
  page.on('pageerror', error => errors.push(error.message));
  await page.request.post(`${base}/demo/sign-in/admin`);
  const enter = async path => {
    await page.goto(`${base}${path}`);
    await page.waitForFunction(() => !!JSON.parse(history.state?.userState ?? '{}').domainListEntry);
    await page.getByRole('button', {name: /^Filters \(/}).waitFor();
  };
  const testTag = `Inventory browser ${Date.now()}`;
  await page.goto(`${base}/tags`);
  await page.waitForFunction(() => [...document.querySelectorAll('button')].some(button => button.textContent.trim() === 'Add tag' && !button.disabled));
  await page.getByLabel('Tag name', {exact: true}).fill(testTag);
  await page.getByRole('button', {name: 'Add tag', exact: true}).click();
  await page.getByText(testTag, {exact: true}).waitFor();
  await enter('/domains');
  await page.getByRole('switch', {name: 'Show management columns', exact: true}).check();
  const tagsForDomain = page.getByLabel('Tags for aurora-retail.example', {exact: true});
  await tagsForDomain.click();
  await page.getByRole('checkbox', {name: testTag, exact: true}).check();
  await page.keyboard.press('Escape');
  await page.waitForFunction(name => [...document.querySelectorAll('input')].some(input => input.value === name), testTag);
  await enter('/domains');
  await page.getByRole('button', {name: 'By group', exact: true}).click();
  await page.waitForFunction(() => document.querySelectorAll('section .domain-inventory').length >= 3);
  assert.equal(await page.getByRole('button', {name: /^Filters \(/}).count(), 1);
  assert.equal(await page.locator('#domain-filters').count(), 1);
  assert.equal(await page.getByLabel('Group', {exact: true}).count(), 1);
  assert.equal(await page.getByLabel('Tag', {exact: true}).count(), 1);
  await page.getByRole('button', {name: 'Sort by Pass rate (30d)', exact: true}).first().click();
  await page.waitForURL(url => url.searchParams.get('sort') === 'pass');
  await page.waitForFunction(() => [...document.querySelectorAll('section')].every(section => {
    const values = [...section.querySelectorAll('[data-label="Pass rate (30d)"]')].map(cell => parseFloat(cell.textContent) || -1);
    return values.every((value, index) => index === 0 || values[index - 1] <= value);
  }));
  await page.getByRole('button', {name: /^Filters \(/}).click();
  await page.getByLabel('Domain search', {exact: true}).fill('aurora');
  await page.waitForFunction(() => [...document.querySelectorAll('a[data-domain-link]')].every(link => link.textContent.includes('aurora')));
  const groupedIds = [...new Set(await page.locator('a[data-domain-link]').allTextContents())].sort();
  assert(groupedIds.length > 0);
  await page.getByRole('button', {name: 'All domains', exact: true}).click();
  await page.waitForFunction(() => document.querySelectorAll('section .domain-inventory').length === 0);
  assert.deepEqual((await page.locator('a[data-domain-link]').allTextContents()).sort(), groupedIds);
  await page.getByRole('button', {name: /^Filters \(/}).click();
  await page.getByRole('button', {name: 'Filters (1)', exact: true}).waitFor();
  assert.deepEqual((await page.locator('a[data-domain-link]').allTextContents()).sort(), groupedIds);

  await enter('/domains?filters=1&view=group&open=true');
  await page.getByLabel('Tag', {exact: true}).click();
  const firstTag = page.locator('.mud-popover-open .mud-list-item').first();
  await firstTag.waitFor();
  await firstTag.click();
  await page.waitForURL(url => url.searchParams.has('tag'));
  await page.getByLabel('Minimum pass rate (%)', {exact: true}).fill('95');
  await page.getByLabel('Minimum pass rate (%)', {exact: true}).press('Tab');
  await page.waitForURL(url => url.searchParams.get('minPass') === '95');
  const tagMatches = [...new Set(await page.locator('a[data-domain-link]').allTextContents())].sort();
  await page.getByRole('button', {name: 'All domains', exact: true}).click();
  await page.waitForFunction(() => document.querySelectorAll('section .domain-inventory').length === 0);
  assert.deepEqual((await page.locator('a[data-domain-link]').allTextContents()).sort(), tagMatches);
  await page.getByRole('button', {name: 'Save as my default', exact: true}).click();
  await page.getByText('Default saved for this page.', {exact: true}).waitFor();
  await enter('/dashboard');
  assert(!new URL(page.url()).searchParams.has('tag'));
  await enter('/domains');
  await page.getByRole('button', {name: 'Filters (2)', exact: true}).waitFor();
  await page.getByRole('button', {name: 'Clear my default', exact: true}).click();
  await page.getByText('Default cleared for this page.', {exact: true}).waitFor();

  const newTabPromise = page.context().waitForEvent('page');
  await page.locator('a[data-domain-link]').first().click({modifiers: ['Control']});
  const newTab = await newTabPromise;
  await newTab.getByRole('button', {name: 'Back', exact: true}).waitFor();
  await newTab.getByRole('button', {name: 'Back', exact: true}).click();
  await newTab.waitForURL(url => url.pathname === '/domains');
  await newTab.close();

  await enter('/domains?filters=1&view=group&sort=pass&desc=true');
  await page.locator('section .domain-inventory').first().waitFor();
  await page.evaluate(() => window.scrollTo(0, 500));
  const before = await page.evaluate(() => window.scrollY);
  const firstDomain = page.locator('a[data-domain-link]').first();
  // Dispatch a normal link click without auto-scrolling the list to its first row.
  await firstDomain.evaluate(link => link.click());
  await page.waitForURL(/\/domains\/[^?]+/);
  await page.getByRole('button', {name: 'Back', exact: true}).click();
  await page.waitForURL(/\/domains\?/);
  await page.waitForFunction(expected => Math.abs(window.scrollY - expected) < 10, before);
  assert.equal(new URL(page.url()).searchParams.get('sort'), 'pass');
  await page.locator('a[data-domain-link]').first().evaluate(link => link.click());
  await page.waitForURL(/\/domains\/[^?]+/);
  await page.goBack();
  await page.waitForFunction(expected => Math.abs(window.scrollY - expected) < 10, before);
  assert((await page.locator('section .domain-inventory').count()) >= 3);
  await page.getByRole('link', {name: 'Inventory', exact: true}).click();
  await page.waitForURL(url => url.pathname === '/domains' && url.search === '');
  await page.waitForFunction(() => window.scrollY < 10 && document.querySelectorAll('section .domain-inventory').length === 0);

  await enter('/domains');
  await page.waitForFunction(() => window.scrollY < 10);
  await enter('/domains?target=below');
  assert((await page.locator('a[data-domain-link]').count()) > 0);
  await enter('/dashboard');
  assert.equal(await page.locator('a[data-domain-link]').count(), 0);
  assert.equal(await page.getByRole('button', {name: /^Filters \(/}).count(), 1);
  await page.goto(`${base}/dashboard?target=below`);
  await page.waitForURL(url => url.pathname === '/domains' && url.searchParams.get('target') === 'below');

  await page.setViewportSize({width: 390, height: 844});
  await enter('/domains?filters=1&view=group');
  await page.getByRole('button', {name: /^Filters \(/}).click();
  await page.getByLabel('Domain search', {exact: true}).fill('aurora');
  await page.waitForFunction(() => [...document.querySelectorAll('a[data-domain-link]')].every(link => link.textContent.includes('aurora')));
  assert.equal(await page.getByRole('button', {name: /^Filters \(/}).count(), 1);
  assert(await page.locator('.domain-percentage').evaluateAll(nodes => nodes.every(node => getComputedStyle(node).whiteSpace === 'nowrap')));

  const viewer = await browser.newPage();
  await viewer.request.post(`${base}/demo/sign-in/viewer`);
  await viewer.goto(`${base}/domains?group=all&view=group`);
  await viewer.waitForFunction(() => !!JSON.parse(history.state?.userState ?? '{}').domainListEntry);
  assert((await viewer.locator('a[data-domain-link]').allTextContents()).every(name => name.includes('aurora')));
  assert.equal(await viewer.getByRole('button', {name: 'Add domain', exact: true}).count(), 0);
  assert.equal(await viewer.getByRole('switch').count(), 1); // management column visibility, not domain mutation
  await viewer.close();
  await page.goto(`${base}/tags`);
  await page.waitForFunction(() => [...document.querySelectorAll('button')].some(button => button.textContent.trim() === 'Add tag' && !button.disabled));
  const tagRow = page.getByRole('row').filter({hasText: testTag});
  await tagRow.getByRole('button', {name: `Remove ${testTag}`, exact: true}).click();
  await page.getByRole('button', {name: /^(Remove|Delete)( tag)?$/, exact: true}).click();
  await tagRow.waitFor({state: 'detached'});
  assert.deepEqual(errors, []);
  console.log('Inventory browser checks passed: one global panel, grouped membership, collapse, shared sort, Back/browser Back, fresh visits, target links, mobile and scoped Viewer.');
} finally {
  await browser.close();
}
