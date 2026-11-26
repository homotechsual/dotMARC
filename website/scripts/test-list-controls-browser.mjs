import assert from 'node:assert/strict';
import {chromium} from 'playwright';

const base = process.env.INVENTORY_TEST_URL ?? 'http://127.0.0.1:5187';
const browser = await chromium.launch({channel: 'msedge', headless: true});
const errors = [];
try {
  const page = await browser.newPage({viewport: {width: 1366, height: 1000}});
  page.on('pageerror', error => errors.push(error.message));
  await page.request.post(`${base}/demo/sign-in/admin`);
  const enter = async (path, label) => {
    await page.goto(`${base}${path}`);
    const toolbar = page.getByRole('group', {name: label, exact: true});
    await toolbar.waitFor();
    await page.waitForFunction(label => document.querySelector(`[aria-label="${label}"]`)?.dataset.interactive === 'true', label);
    await toolbar.locator('input:not([type="hidden"])').first().waitFor();
    return toolbar;
  };
  const checkLayout = async toolbar => {
    for (const width of [1366, 768, 390, 320]) {
      await page.setViewportSize({width, height: 1000});
      const bounds = await toolbar.evaluate(node => {
        const controls = [...node.querySelectorAll('input:not([type="hidden"]),[role="combobox"],button')]
          .filter(element => element.checkVisibility()).map(element => {
            const rect = element.getBoundingClientRect();
            return {left: rect.left, right: rect.right, top: rect.top, bottom: rect.bottom};
          });
        const order = [...node.children].filter(element => element.checkVisibility()).map(element => {
          const rect = element.getBoundingClientRect();
          return {left: rect.left, right: rect.right, top: rect.top, bottom: rect.bottom};
        });
        return {width: innerWidth, controls, order};
      });
      assert(bounds.controls.every(control => control.left >= 0 && control.right <= width + 1),
        JSON.stringify({width, bounds}));
      assert(bounds.order.every((control, index) => !index || control.left >= bounds.order[index - 1].right - 1
        || control.top >= bounds.order[index - 1].bottom - 1),
      `Toolbar order must follow search, sort, filters, actions: ${JSON.stringify({width, bounds})}`);
      const filter = toolbar.getByRole('button', {name: /^Filters \(/});
      if (await filter.count()) {
        await filter.click();
        const panel = page.locator(`#${await filter.getAttribute('aria-controls')}`);
        await panel.waitFor();
        const fits = await panel.evaluate(node => [...node.querySelectorAll('input:not([type="hidden"]),[role="combobox"],button')]
          .filter(element => element.checkVisibility()).every(element => {
            const rect = element.getBoundingClientRect();
            return rect.left >= 0 && rect.right <= innerWidth + 1;
          }));
        assert(fits, `Expanded filters must fit at ${width}px`);
        await filter.click();
      }
    }
    await page.setViewportSize({width: 1366, height: 1000});
  };
  const checkTableSort = async toolbar => {
    const table = toolbar.locator('xpath=ancestor::div[contains(concat(" ",normalize-space(@class)," ")," mud-table ")][1]');
    const sort = toolbar.getByLabel('Sort', {exact: true});
    if (!await table.count() || !await sort.count()) return;
    const headers = table.locator('thead .mud-table-sort-label');
    const name = (await headers.first().innerText()).trim();
    await sort.click();
    await page.getByRole('option', {name: `${name} (descending)`, exact: true}).click();
    await headers.first().locator('.mud-direction-desc').waitFor();
    const second = headers.nth(await headers.count() > 1 ? 1 : 0);
    const secondName = (await second.innerText()).trim();
    await second.click();
    await page.waitForFunction(({label, expected}) => [...document.querySelector(`[aria-label="${label}"]`).querySelectorAll('input')]
      .some(input => input.value === expected), {label: await toolbar.getAttribute('aria-label'), expected: `${secondName} (ascending)`});
    await sort.click();
    await page.getByRole('option', {name: 'Default order', exact: true}).click();
    await page.waitForFunction(label => [...document.querySelector(`[aria-label="${label}"]`).querySelectorAll('input')]
      .some(input => input.value === 'Default order'), await toolbar.getAttribute('aria-label'));
  };

  for (const [path, label] of [
    ['/groups', 'Group controls'],
    ['/tags', 'Tag controls'],
    ['/access/people', 'People controls'],
    ['/access/roles', 'Role controls'],
    ['/access/api-keys', 'API key controls'],
    ['/alerts', 'Alert controls'],
    ['/parse-failures', 'Parse failure controls'],
    ['/logs/audit', 'Audit log controls'],
    ['/alerts/rules', 'Alert rule controls'],
    ['/alerts/settings', 'Alert destination controls'],
    ['/domains/aurora-retail.example/sources', 'Source IP controls'],
    ['/domains/aurora-retail.example/raw-reports', 'Raw report controls'],
    ['/domains/aurora-retail.example/tls-reports', 'TLS report controls'],
  ]) {
    console.log(`Checking ${label}`);
    const toolbar = await enter(path, label);
    await checkLayout(toolbar);
    await checkTableSort(toolbar);
  }
  await page.goto(`${base}/baselines/domain`);
  await page.locator('summary').filter({hasText: 'group targets and edit overrides'}).click();
  await checkLayout(page.getByRole('group', {name: 'Group target controls', exact: true}));
  await page.goto(`${base}/baselines/sender`);
  await page.locator('summary').filter({hasText: 'services and set decisions'}).click();
  await checkLayout(page.getByRole('group', {name: 'Sender default controls', exact: true}));
  await page.goto(`${base}/domains/aurora-retail.example/sources`);
  await checkLayout(page.getByRole('group', {name: 'Sender controls', exact: true}));
  await page.goto(`${base}/domains/import`);
  await page.waitForFunction(() => !document.querySelector('#add-domain input')?.disabled);
  await page.getByLabel('Domains', {exact: true}).fill('list-controls-new.example\nnot a domain\naurora-retail.example');
  await page.getByRole('button', {name: 'Preview', exact: true}).click();
  await checkLayout(page.getByRole('group', {name: 'Import preview controls', exact: true}));
  await page.getByLabel('Search preview domains', {exact: true}).fill('not a domain');
  const preview = page.getByRole('group', {name: 'Import preview controls', exact: true});
  await preview.getByRole('button', {name: 'Filters (0)', exact: true}).click();
  await page.getByLabel('Status', {exact: true}).click();
  await page.getByRole('option', {name: 'Invalid', exact: true}).click();
  assert.equal(await page.locator('.mud-table tbody tr').count(), 1);
  assert.match(await page.getByRole('button', {name: /^Import: add/}).innerText(), /add 1, update 0/i);

  const tags = await enter('/tags', 'Tag controls');
  const stamp = Date.now();
  for (const suffix of ['A', 'Z']) {
    if (!await page.getByLabel('Tag name', {exact: true}).isVisible())
      await tags.getByRole('button', {name: 'Add tag', exact: true}).click();
    await page.getByLabel('Tag name', {exact: true}).fill(`List sorting ${stamp} ${suffix}`);
    await page.locator('#add-tag').getByRole('button', {name: 'Add tag', exact: true}).click();
    await page.getByText(`List sorting ${stamp} ${suffix}`, {exact: true}).waitFor();
  }
  await tags.getByRole('button', {name: 'Close add tag', exact: true}).click();
  const sort = tags.getByLabel('Sort', {exact: true});
  await sort.click();
  await page.getByRole('option', {name: 'Name (descending)', exact: true}).click();
  await page.waitForFunction(() => {
    const names = [...document.querySelectorAll('tbody tr')].map(row => row.querySelector('td')?.textContent.trim());
    return names.length > 1 && names.every((name, index) => !index || names[index - 1].localeCompare(name) >= 0);
  });
  await page.locator('thead .mud-table-sort-label').filter({hasText: /^Color$/}).click();
  await page.waitForFunction(() => [...document.querySelectorAll('.list-toolbar input')].some(input => input.value === 'Color (ascending)'));
  await sort.click();
  await page.getByRole('option', {name: 'Default order', exact: true}).click();
  await page.waitForFunction(() => [...document.querySelectorAll('.list-toolbar input')].some(input => input.value === 'Default order'));
  const search = tags.getByLabel('Search tags', {exact: true});
  await search.fill('missing-list-test');
  await tags.getByRole('button', {name: 'Filters (0)', exact: true}).click();
  const panel = page.locator(`#${await tags.getByRole('button', {name: /^Filters/}).getAttribute('aria-controls')}`);
  await panel.getByLabel('Minimum domains', {exact: true}).fill('10000');
  await panel.getByLabel('Minimum domains', {exact: true}).press('Tab');
  await tags.getByRole('button', {name: 'Filters (1)', exact: true}).waitFor();
  await tags.getByRole('button', {name: 'Filters (1)', exact: true}).click();
  await tags.getByRole('button', {name: 'Filters (1)', exact: true}).click();
  assert.equal(await panel.getByLabel('Minimum domains', {exact: true}).inputValue(), '10000');
  await panel.getByRole('button', {name: 'Clear filters', exact: true}).click();
  await tags.getByRole('button', {name: 'Filters (0)', exact: true}).waitFor();
  assert.equal(await search.inputValue(), 'missing-list-test');

  for (const [path, label, action, input] of [
    ['/tags', 'Tag controls', 'Add tag', 'Tag name'],
    ['/groups', 'Group controls', 'Add group', 'Group name'],
    ['/access/people', 'People controls', 'Grant access', 'Email'],
    ['/access/roles', 'Role controls', 'Add role', 'Role name'],
    ['/access/api-keys', 'API key controls', 'Create key', 'Name'],
  ]) {
    const toolbar = await enter(path, label);
    assert.equal(await page.getByLabel(input, {exact: true}).isVisible(), false);
    await toolbar.getByRole('button', {name: action, exact: true}).click();
    await page.getByLabel(input, {exact: true}).waitFor();
    const close = toolbar.getByRole('button', {name: /^Close/});
    await close.click();
    await page.getByLabel(input, {exact: true}).waitFor({state: 'hidden'});
    assert.equal(await page.getByLabel(input, {exact: true}).isVisible(), false);
  }

  await page.goto(`${base}/domains#add-domain`);
  await page.getByLabel('Domain name', {exact: true}).waitFor();
  await page.getByRole('button', {name: 'Close add domain', exact: true}).click();
  await page.getByLabel('Domain name', {exact: true}).waitFor({state: 'hidden'});
  assert.equal(await page.getByLabel('Domain name', {exact: true}).isVisible(), false);
  await page.request.post(`${base}/demo/sign-in/viewer`);
  await page.goto(`${base}/domains`);
  await page.getByLabel('Domain search', {exact: true}).waitFor();
  assert.equal(await page.getByRole('button', {name: 'Add domain', exact: true}).count(), 0);
  assert.deepEqual(errors, []);
  console.log('Unified list controls: layout, filtering, sorting, creation and permissions passed.');
} finally {
  await browser.close();
}
