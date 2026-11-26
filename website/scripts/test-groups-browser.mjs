import assert from 'node:assert/strict';
import {chromium} from 'playwright';

const base = process.env.INVENTORY_TEST_URL ?? 'http://127.0.0.1:5187';
const browser = await chromium.launch({channel: 'msedge', headless: true});
const errors = [];
try {
  const page = await browser.newPage({viewport: {width: 1440, height: 1100}});
  page.on('pageerror', error => errors.push(error.message));
  await page.request.post(`${base}/demo/sign-in/admin`);
  const ready = async () => {
    await page.goto(`${base}/groups`);
    await page.getByRole('button', {name: 'Filters (0)', exact: true}).waitFor();
    await page.waitForFunction(() => {
      const cards = document.querySelector('.group-cards');
      const filters = document.querySelector('[aria-controls="group-filters"]');
      return cards && getComputedStyle(cards).display === 'grid' && filters && !filters.disabled;
    });
    await page.getByRole('button', {name: 'Filters (0)', exact: true}).click();
    await page.getByLabel('Group search', {exact: true}).fill('Aurora');
    await page.waitForFunction(() => document.querySelectorAll('.group-card').length === 1);
  };
  await ready();
  const controlPositions = await page.locator('[aria-controls="add-group"]').evaluate(add => {
    const row = add.closest('.group-list-toolbar');
    return [...row.querySelectorAll('input:not([type="hidden"]),[role="combobox"],[aria-controls="group-filters"],[aria-controls="add-group"]')].map(node => node.getBoundingClientRect().left);
  });
  assert.equal(controlPositions.length, 4);
  assert(controlPositions.every((left, index) => !index || left > controlPositions[index - 1]), 'Controls must be search, sort, filters, add');
  const onboarding = page.locator('.group-company-onboarding');
  assert.equal(await onboarding.getAttribute('open'), null);
  await onboarding.locator('summary').click();
  await onboarding.getByRole('button', {name: 'Filters', exact: true}).click();
  await onboarding.getByPlaceholder('Search', {exact: true}).fill('Fabrikam');
  await page.waitForFunction(() => document.querySelectorAll('.group-company-onboarding tbody tr').length === 1);
  await onboarding.getByText('Fabrikam Inc', {exact: true}).waitFor();
  await onboarding.getByPlaceholder('Search', {exact: true}).fill('');
  await onboarding.locator('summary').click();
  const card = page.getByRole('article', {name: 'Group Aurora Retail', exact: true});
  await card.getByText('Monthly', {exact: true}).waitFor();
  await card.locator('summary').click();
  const haloConnection = card.locator('.group-connection-grid .group-card-section').first();
  await haloConnection.locator('input').click();
  await page.getByRole('option', {name: 'Aurora Retail', exact: true}).click();
  await card.locator('summary').filter({hasText: '(1 linked)'}).waitFor();
  await card.locator('summary').click();
  const filter = page.locator('#group-filters');
  await page.getByRole('button', {name: 'Filters (1)', exact: true}).click();
  assert.equal(await page.locator('.group-card').count(), 1);
  await page.getByRole('button', {name: 'Filters (1)', exact: true}).click();
  await filter.getByLabel('Minimum domains', {exact: true}).fill('1000');
  await filter.getByLabel('Minimum domains', {exact: true}).press('Tab');
  await page.getByText('No groups match these filters.', {exact: false}).waitFor();
  await page.getByRole('button', {name: 'Clear group filters', exact: true}).click();
  await page.waitForFunction(() => document.querySelectorAll('.group-card').length === 12);
  await filter.getByLabel('HaloPSA company link', {exact: true}).click();
  await page.getByRole('option', {name: 'Not linked', exact: true}).click();
  await page.getByRole('button', {name: 'Filters (1)', exact: true}).waitFor();
  const unlinked = await page.locator('.group-card').evaluateAll(cards => cards.map(card => card.getAttribute('aria-label')));
  assert(unlinked.length > 0 && unlinked.length < 12);
  await filter.getByLabel('HaloPSA company link', {exact: true}).click();
  await page.getByRole('option', {name: 'Linked', exact: true}).click();
  await page.waitForFunction(count => document.querySelectorAll('.group-card').length === 12 - count, unlinked.length);
  const linked = await page.locator('.group-card').evaluateAll(cards => cards.map(card => card.getAttribute('aria-label')));
  assert(!linked.some(name => unlinked.includes(name)));
  await page.getByRole('button', {name: 'Clear group filters', exact: true}).click();
  await filter.getByLabel('Ticket rule overrides', {exact: true}).click();
  await page.getByRole('option', {name: 'Has overrides', exact: true}).click();
  await page.getByRole('button', {name: 'Filters (1)', exact: true}).waitFor();
  assert(await page.locator('.group-card').count() < 12);
  await page.getByRole('button', {name: 'Clear group filters', exact: true}).click();
  await page.getByLabel('Sort groups', {exact: true}).click();
  await page.getByRole('option', {name: 'Most domains', exact: true}).click();
  await page.waitForFunction(() => {
    const counts = [...document.querySelectorAll('.group-identity a')].map(link => parseInt(link.textContent));
    return counts.length === 12 && counts.every((count, index) => !index || count <= counts[index - 1]);
  });
  await page.getByLabel('Sort groups', {exact: true}).click();
  await page.getByRole('option', {name: 'Name (Z to A)', exact: true}).click();
  await page.waitForFunction(() => {
    const names = [...document.querySelectorAll('.group-card')].map(card => card.getAttribute('aria-label'));
    return names.length === 12 && names.every((name, index) => !index || names[index - 1].localeCompare(name) >= 0);
  });
  await page.getByLabel('Group search', {exact: true}).fill('Aurora');
  await page.waitForFunction(() => document.querySelectorAll('.group-card').length === 1);
  await page.getByRole('button', {name: 'Filters (1)', exact: true}).click();

  const openDialog = async (label, heading, close) => {
    await card.getByRole('button', {name: label, exact: true}).click();
    const dialog = page.getByRole('dialog');
    await dialog.getByText(heading, {exact: true}).waitFor();
    await dialog.locator('.mud-dialog-actions').getByRole('button', {name: close, exact: true}).click();
    await dialog.waitFor({state: 'hidden'});
  };
  await openDialog('Reports for Aurora Retail', 'Client reports for Aurora Retail', 'Close');
  await openDialog('Domain baseline for Aurora Retail', 'DMARC target for Aurora Retail', 'Cancel');
  await openDialog('Portal branding for Aurora Retail', 'Portal branding for Aurora Retail', 'Cancel');
  await card.locator('summary').click();
  await card.getByRole('button', {name: 'Ticket rules for Aurora Retail', exact: true}).click();
  await page.getByRole('dialog').waitFor();
  await page.getByRole('dialog').getByRole('button', {name: 'Done', exact: true}).click();
  await page.getByRole('dialog').waitFor({state: 'hidden'});
  await card.locator('summary').click();
  if (process.env.GROUPS_DESKTOP_SCREENSHOT) await page.screenshot({path: process.env.GROUPS_DESKTOP_SCREENSHOT, fullPage: true});

  for (const width of [768, 390, 320]) {
    await page.setViewportSize({width, height: 1000});
    for (const expanded of [false, true]) {
      if (expanded) await card.locator('summary').click();
      const bounds = await card.evaluate(node => {
        const rect = node.getBoundingClientRect();
        return {left: rect.left, right: rect.right, width: innerWidth, scroll: node.scrollWidth, client: node.clientWidth,
          controls: [...node.querySelectorAll('button,input,a')].filter(element => element.checkVisibility()).map(element => {
            const box = element.getBoundingClientRect();
            return box.left >= rect.left - 1 && box.right <= rect.right + 1;
          })};
      });
      assert(bounds.left >= 0 && bounds.right <= bounds.width && bounds.scroll <= bounds.client + 1 && bounds.controls.every(Boolean), JSON.stringify({width, expanded, bounds}));
    }
    await openDialog('Reports for Aurora Retail', 'Client reports for Aurora Retail', 'Close');
    await openDialog('Domain baseline for Aurora Retail', 'DMARC target for Aurora Retail', 'Cancel');
    if (width === 390 && process.env.GROUPS_MOBILE_SCREENSHOT) await page.screenshot({path: process.env.GROUPS_MOBILE_SCREENSHOT, fullPage: true});
    await card.locator('summary').click();
  }
  await card.getByRole('button', {name: 'Sender defaults for Aurora Retail', exact: true}).click();
  await page.waitForURL(/\/groups\/\d+\/senders/);
  await page.getByText('Aurora Retail', {exact: false}).first().waitFor();
  await ready();
  await card.locator('summary').click();
  await haloConnection.locator('.mud-input-clear-button').click();
  await card.locator('summary').filter({hasText: '(0 linked)'}).waitFor();
  await card.getByRole('link', {name: 'View domains in Aurora Retail', exact: true}).click();
  await page.waitForURL(url => url.pathname === '/domains' && url.searchParams.has('group'));

  await ready();
  const name = `Groups browser ${Date.now()}`;
  assert.equal(await page.locator('#add-group').isVisible(), false);
  await page.getByRole('button', {name: 'Add group', exact: true}).click();
  await page.getByLabel('Group name', {exact: true}).fill(name);
  await page.getByRole('button', {name: 'Add group', exact: true}).click();
  await page.getByRole('button', {name: 'Filters (1)', exact: true}).click();
  await page.getByLabel('Group search', {exact: true}).fill(name);
  const created = page.getByRole('article', {name: `Group ${name}`, exact: true});
  await created.waitFor();
  await created.getByRole('textbox', {name: `Rename ${name}`, exact: true}).fill(`${name} renamed`);
  await created.getByRole('textbox', {name: `Rename ${name}`, exact: true}).press('Tab');
  const renamed = page.getByRole('article', {name: `Group ${name} renamed`, exact: true});
  await renamed.waitFor();
  await renamed.getByRole('button', {name: `Remove group ${name} renamed`, exact: true}).click();
  await page.getByRole('dialog').getByRole('button', {name: 'Remove', exact: true}).click();
  await page.getByRole('dialog').waitFor({state: 'hidden'});
  await renamed.waitFor({state: 'hidden'});

  await page.request.post(`${base}/demo/sign-in/viewer`);
  await page.goto(`${base}/groups`);
  assert.equal(await page.locator('.group-card').count(), 0);
  assert.deepEqual(errors, []);
  console.log('Groups browser checks passed: filters/collapse/clear, sorting, report/baseline/branding dialogs, PSA linking/expansion, company search, group create/rename/delete, sender and Inventory navigation, 320/390/768px card geometry and read-only access.');
} finally {
  await browser.close();
}
