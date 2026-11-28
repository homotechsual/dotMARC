import assert from 'node:assert/strict';
import {chromium} from 'playwright';

const base = process.env.INVENTORY_TEST_URL ?? 'http://127.0.0.1:5187';
const browser = await chromium.launch({channel: 'msedge', headless: true});
const page = await browser.newPage({viewport: {width: 1366, height: 900}});
const errors = [];
page.on('pageerror', error => errors.push(error.message));
const stamp = Date.now();
const prefix = `bulk-${stamp}`;
const names = [0, 1, 2].map(index => `${prefix}-${index}.example`);
const tag = `Bulk browser ${stamp}`;
const ready = () => page.waitForFunction(() => !!JSON.parse(history.state?.userState ?? '{}').domainListEntry);
const dialog = () => page.getByRole('dialog').last();
const targetDialog = () => page.getByRole('dialog').filter({has: page.getByRole('button', {name: 'Save target', exact: true})});
const openBulk = async action => {
  await page.getByRole('button', {name: 'Bulk actions', exact: true}).click();
  await dialog().getByLabel('Bulk action', {exact: true}).click();
  await page.getByRole('option', {name: action, exact: true}).click();
};
const apply = async () => {
  await dialog().getByRole('button', {name: 'Apply to selected domains', exact: true}).click();
  await dialog().getByText(/2 of 2 processed: 2 completed, 0 skipped, 0 failed. 0 not attempted./).waitFor();
  await dialog().getByRole('button', {name: 'Close', exact: true}).click();
};
const assignment = async (label, name) => {
  await dialog().getByLabel(label, {exact: true}).click();
  const choice = page.getByRole('checkbox', {name, exact: true});
  await choice.check();
  await choice.press('Escape');
};
try {
  await page.request.post(`${base}/demo/sign-in/admin`);
  await page.goto(`${base}/tags`);
  await page.waitForFunction(() => document.querySelector('[aria-label="Tag controls"]')?.dataset.interactive === 'true');
  await page.getByRole('button', {name: 'Add tag', exact: true}).click();
  await page.getByLabel('Tag name', {exact: true}).fill(tag);
  await page.locator('#add-tag').getByRole('button', {name: 'Add tag', exact: true}).click();
  await page.getByText(tag, {exact: true}).waitFor();

  for (const name of names) {
    await page.goto(`${base}/domains/import`);
    const form = page.locator('#add-domain');
    await page.waitForFunction(() => [...document.querySelectorAll('#add-domain button')].some(button =>
      button.textContent.trim() === 'Add domain' && !button.disabled));
    await form.getByRole('checkbox', {name: 'Open domain and set up DNS', exact: true}).uncheck();
    await form.getByLabel('Domain name', {exact: true}).fill(name);
    await form.getByRole('button', {name: 'Add domain', exact: true}).click();
    await form.getByRole('link', {name: 'View domain', exact: true}).waitFor();
  }

  await page.goto(`${base}/domains/${names[2]}`);
  const remove = page.getByRole('button', {name: 'Remove domain', exact: true});
  await page.waitForFunction(() => [...document.querySelectorAll('button')].some(button =>
    button.textContent.trim() === 'Remove domain' && !button.disabled));
  await remove.click();
  await dialog().getByText(/Permanently remove/).waitFor();
  await dialog().getByRole('button', {name: 'Cancel', exact: true}).click();
  assert.equal(new URL(page.url()).pathname, `/domains/${names[2]}`);
  await remove.click();
  await dialog().getByRole('button', {name: 'Remove', exact: true}).click();
  await page.waitForURL(url => url.pathname === '/domains');
  await ready();
  await page.getByLabel('Domain search', {exact: true}).fill(prefix);
  await page.waitForFunction(() => document.querySelectorAll('a[data-domain-link]').length === 2);
  await page.getByRole('button', {name: 'Select domains', exact: true}).click();
  await page.getByRole('menuitem', {name: 'Select visible (2)', exact: true}).click();
  await page.getByText('2 selected domains', {exact: true}).waitFor();

  await openBulk('Add group membership');
  await assignment('Groups to change', 'Aurora Retail');
  await apply();
  await openBulk('Add group membership');
  await assignment('Groups to change', 'Cobalt Freight');
  await apply();
  await openBulk('Remove group membership');
  await assignment('Groups to change', 'Cobalt Freight');
  await apply();
  await openBulk('Add tags');
  await assignment('Tags to change', tag);
  await apply();
  await openBulk('Remove tags');
  await assignment('Tags to change', tag);
  await apply();

  await openBulk('Set DMARC target');
  const applyTarget = dialog().getByRole('button', {name: 'Apply to selected domains', exact: true});
  assert(await applyTarget.isDisabled(), 'No implicit target clearing before choosing a target');
  await dialog().getByRole('button', {name: 'Choose DMARC target', exact: true}).click();
  await targetDialog().getByRole('button', {name: 'Cancel', exact: true}).click();
  assert(await applyTarget.isDisabled(), 'Cancelling the picker must not clear existing targets');
  await dialog().getByRole('button', {name: 'Choose DMARC target', exact: true}).click();
  await targetDialog().getByLabel('Policy (p)', {exact: true}).click();
  await page.getByRole('option', {name: 'Reject', exact: true}).click();
  await targetDialog().getByRole('button', {name: 'Save target', exact: true}).click();
  await apply();
  await openBulk('Set DMARC target');
  await dialog().getByRole('button', {name: 'Choose DMARC target', exact: true}).click();
  await targetDialog().getByRole('button', {name: 'Save target', exact: true}).click();
  await dialog().getByText('Inherit group or system target (clear domain overrides)', {exact: true}).waitFor();
  await apply();

  await openBulk('Remove domains');
  const confirm = dialog().getByLabel('Type REMOVE to confirm', {exact: true});
  assert(await dialog().getByRole('button', {name: 'Apply to selected domains', exact: true}).isDisabled());
  await confirm.fill('remove');
  assert(await dialog().getByRole('button', {name: 'Apply to selected domains', exact: true}).isDisabled());
  for (const width of [768, 390, 320]) {
    await page.setViewportSize({width, height: 900});
    const bounds = await dialog().evaluate(node => {
      const box = node.getBoundingClientRect();
      return {left: box.left, right: box.right, width: innerWidth, scroll: node.scrollWidth, client: node.clientWidth};
    });
    assert(bounds.left >= 0 && bounds.right <= bounds.width && bounds.scroll <= bounds.client + 1,
      `Bulk dialog overflows at ${width}px: ${JSON.stringify(bounds)}`);
  }
  await confirm.fill('REMOVE');
  await apply();
  await page.getByText('0 selected domains', {exact: true}).waitFor();
  assert.equal(await page.getByRole('checkbox', {name: new RegExp(`^Select ${prefix}`)}).count(), 0);

  await page.setViewportSize({width: 1366, height: 900});
  await page.request.post(`${base}/demo/sign-in/viewer`);
  await page.goto(`${base}/domains`);
  await ready();
  assert.equal(await page.getByRole('button', {name: 'Bulk actions', exact: true}).count(), 0);
  await page.goto(`${base}/domains/aurora-retail.example`);
  await page.getByRole('button', {name: 'Back', exact: true}).waitFor();
  assert.equal(await page.getByRole('button', {name: 'Remove domain', exact: true}).count(), 0);
  assert.deepEqual(errors, []);
  console.log('Bulk domain browser checks passed: single remove/cancel, group and tag add/remove, target cancel/save/inherit, destructive confirmation, two-domain results, 768/390/320px dialogs and viewer isolation.');
} finally {
  try {
    await page.request.post(`${base}/demo/sign-in/admin`);
    await page.goto(`${base}/domains`);
    await ready();
    for (const name of names) {
      const checkbox = page.getByRole('checkbox', {name: `Select ${name}`, exact: true});
      if (await checkbox.count()) await checkbox.check();
    }
    if (await page.getByRole('button', {name: 'Bulk actions', exact: true}).isEnabled()) {
      await openBulk('Remove domains');
      await dialog().getByLabel('Type REMOVE to confirm', {exact: true}).fill('REMOVE');
      await dialog().getByRole('button', {name: 'Apply to selected domains', exact: true}).click();
      await dialog().getByRole('button', {name: 'Close', exact: true}).click();
    }
    await page.goto(`${base}/tags`);
    await page.waitForFunction(() => document.querySelector('[aria-label="Tag controls"]')?.dataset.interactive === 'true');
    const removeTag = page.getByRole('button', {name: `Remove ${tag}`, exact: true});
    if (await removeTag.count()) {
      await removeTag.click();
      await dialog().getByRole('button', {name: 'Remove', exact: true}).click();
      await removeTag.waitFor({state: 'hidden'});
    }
  } finally {
    await browser.close();
  }
}
