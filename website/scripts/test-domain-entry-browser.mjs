import assert from 'node:assert/strict';
import {chromium} from 'playwright';

const base = process.env.INVENTORY_TEST_URL ?? 'http://127.0.0.1:5187';
const browser = await chromium.launch({channel: 'msedge', headless: true});
const errors = [];
try {
  const page = await browser.newPage({viewport: {width: 1440, height: 1000}});
  page.on('pageerror', error => errors.push(error.message));
  await page.request.post(`${base}/demo/sign-in/admin`);
  const ready = async () => {
    await page.waitForFunction(() => [...document.querySelectorAll('#add-domain button')].some(button =>
      button.textContent.trim() === 'Add domain' && !button.disabled));
  };
  await page.goto(`${base}/dashboard?filters=1&open=true`);
  await ready();
  await page.getByLabel('Group', {exact: true}).fill('Aurora');
  await page.getByRole('option', {name: 'Aurora Retail', exact: true}).click();
  await page.waitForURL(url => url.searchParams.has('group'));
  const group = new URL(page.url()).searchParams.get('group');
  const importHref = `/domains/import?group=${group}`;
  await page.waitForFunction(href => [...document.querySelectorAll('a')].some(link =>
    link.textContent.trim() === 'Import domains' && link.getAttribute('href') === href), importHref);
  assert.equal(await page.getByRole('link', {name: 'Import domains', exact: true}).getAttribute('href'), importHref);

  const stamp = Date.now();
  for (const [index, path] of ['/dashboard', '/domains', '/domains/import'].entries()) {
    await page.goto(`${base}${path}?group=${group}`);
    await ready();
    const form = page.locator('#add-domain');
    assert.equal(await form.count(), 1);
    assert.equal(await form.getByLabel('Add to group', {exact: true}).inputValue(), 'Aurora Retail');
    const expectedGroup = index === 1 ? 'Cobalt Freight' : 'Aurora Retail';
    if (index === 1) {
      await form.getByLabel('Add to group', {exact: true}).fill('Cobalt');
      await page.getByRole('option', {name: expectedGroup, exact: true}).click();
      assert.equal(new URL(page.url()).searchParams.get('group'), group);
    }
    await form.getByLabel('Domain name', {exact: true}).fill('not a domain');
    await form.getByRole('button', {name: 'Add domain', exact: true}).click();
    await form.getByText('Enter a valid domain name.', {exact: true}).waitFor();
    const name = `entry-${stamp}-${index}.example`;
    await form.getByLabel('Domain name', {exact: true}).fill(name.toUpperCase());
    await form.getByRole('button', {name: 'Add domain', exact: true}).dblclick();
    await form.getByRole('link', {name: 'View domain', exact: true}).waitFor();
    assert.equal(new URL(page.url()).pathname, path);
    assert.equal(await form.getByRole('link', {name: 'View domain', exact: true}).getAttribute('href'), `/domains/${name}`);
    await form.getByLabel('Domain name', {exact: true}).fill(name);
    await form.getByRole('button', {name: 'Add domain', exact: true}).click();
    await form.getByText('That domain already exists.', {exact: true}).waitFor();
    await page.goto(`${base}/domains/${name}`);
    await page.locator('.domain-groups .mud-typography-body2').waitFor();
    assert.equal(await page.locator('.domain-groups .mud-typography-body2').innerText(), expectedGroup);
    await page.reload();
    await page.locator('.domain-groups .mud-typography-body2').waitFor();
    assert.equal(await page.locator('.domain-groups .mud-typography-body2').innerText(), expectedGroup);
  }

  await page.goto(`${base}/domains/import?group=2147483647`);
  await ready();
  await page.getByLabel('Domain name', {exact: true}).fill(`invalid-group-${stamp}.example`);
  await page.getByRole('button', {name: 'Add domain', exact: true}).click();
  await page.getByText('The selected group no longer exists. Reload the page and choose a group.', {exact: true}).waitFor();
  assert.equal(await page.getByRole('link', {name: 'View domain', exact: true}).count(), 0);

  await page.goto(`${base}/domains?group=${group}#add-domain`);
  await ready();
  await page.waitForFunction(() => document.activeElement === document.querySelector('#add-domain input'));
  await page.getByRole('link', {name: 'Import domains', exact: true}).click();
  await page.waitForURL(`${base}${importHref}`);
  await ready();
  await page.getByLabel('Domains', {exact: true}).fill(`bulk-preview-${stamp}.example`);
  await page.getByRole('button', {name: 'Preview', exact: true}).click();
  await page.getByRole('button', {name: 'Import: add 1, update 0', exact: true}).waitFor();
  assert.equal(await page.locator('#add-domain').count(), 0);
  await page.getByRole('button', {name: 'Back', exact: true}).click();
  await ready();
  assert.equal(await page.getByLabel('Domains', {exact: true}).inputValue(), `bulk-preview-${stamp}.example`);
  await page.setViewportSize({width: 390, height: 844});
  const geometry = await page.locator('#add-domain').evaluate(node => {
    const bounds = node.getBoundingClientRect();
    const controls = [...node.querySelectorAll('input,button')].map(element => element.getBoundingClientRect());
    return {left: bounds.left, right: bounds.right, viewport: innerWidth,
      contained: controls.every(control => control.left >= bounds.left && control.right <= bounds.right)};
  });
  assert(geometry.left >= 0 && geometry.right <= geometry.viewport && geometry.contained);
  if (process.env.ENTRY_SCREENSHOT) await page.screenshot({path: process.env.ENTRY_SCREENSHOT, fullPage: true});
  await page.getByRole('button', {name: 'Preview', exact: true}).click();
  await page.getByRole('button', {name: 'Import: add 1, update 0', exact: true}).click();
  await page.getByText(/Added 1, updated 0/).waitFor();
  assert.equal(await page.locator('#add-domain').count(), 0);
  await page.goto(`${base}/domains/bulk-preview-${stamp}.example`);
  await page.locator('.domain-groups .mud-typography-body2').waitFor();
  assert.equal(await page.locator('.domain-groups .mud-typography-body2').innerText(), 'No groups assigned in your scope.');

  await page.request.post(`${base}/demo/sign-in/viewer`);
  for (const path of ['/dashboard', '/domains']) {
    await page.goto(`${base}${path}`);
    await page.waitForFunction(() => !!JSON.parse(history.state?.userState ?? '{}').domainListEntry);
    assert.equal(await page.locator('#add-domain').count(), 0);
    assert.equal(await page.getByRole('link', {name: 'Import domains', exact: true}).count(), 0);
  }
  await page.goto(`${base}/domains/import`);
  assert.equal(await page.locator('#add-domain').count(), 0);
  assert.deepEqual(errors, []);
  console.log('Domain-entry browser checks passed: inline creation on all three pages, validation, normalization, duplicates, group persistence, bulk preview/save isolation, mobile controls and read-only access.');
} finally {
  await browser.close();
}
