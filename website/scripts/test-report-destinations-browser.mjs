import assert from 'node:assert/strict';
import {chromium} from 'playwright';

const base = process.env.INVENTORY_TEST_URL ?? 'http://127.0.0.1:5187';
const browser = await chromium.launch({channel: 'msedge', headless: true});
const page = await browser.newPage({viewport: {width: 1440, height: 1000}});
const errors = [];
page.on('pageerror', error => errors.push(error.message));
const originals = [];
const reportEditorsReady = () => page.waitForFunction(() => [...document.querySelectorAll('button')].some(button =>
  button.textContent.trim() === 'Save report destinations' && !button.disabled));
const editorFor = async scope => {
  await page.goto(`${base}/baselines/domain`);
  await reportEditorsReady();
  const system = page.locator('.baseline-step').first();
  if (scope === 'system') {
    const editor = system.locator('.dmarc-report-addresses');
    await editor.getByRole('button', {name: 'Save report destinations', exact: true}).waitFor();
    return editor;
  }
  await page.locator('summary').filter({hasText: /^View \d+ group targets and edit overrides$/}).click();
  const row = page.getByRole('row').filter({has: page.getByRole('cell', {name: scope, exact: true})});
  await row.locator('summary').click();
  const editor = row.locator('.dmarc-report-addresses');
  await editor.getByRole('button', {name: 'Save report destinations', exact: true}).waitFor();
  return editor;
};
const save = async editor => {
  await editor.getByRole('button', {name: 'Save report destinations', exact: true}).click();
  await page.getByText('Report destinations saved. Review DNS changes separately.', {exact: true}).last().waitFor();
  await page.waitForFunction(() => ![...document.querySelectorAll('button')].some(button =>
    button.textContent.trim() === 'Save report destinations' && button.disabled));
};
try {
  await page.request.post(`${base}/demo/sign-in/admin`);
  for (const [scope, address] of [
    ['system', 'system-reports@example.com'],
    ['Aurora Retail', 'aurora-reports@example.com'],
    ['Cobalt Freight', 'cobalt-reports@example.com'],
  ]) {
    const editor = await editorFor(scope);
    const primary = editor.getByLabel('Primary report address (configuration)', {exact: true});
    assert.equal(await primary.isDisabled(), true);
    if (scope === 'system') {
      assert.equal(await page.locator('.baseline-step').first().locator('details').count(), 0);
      for (const width of [1440, 390]) {
        await page.setViewportSize({width, height: 1000});
        const bounds = await editor.evaluate(node => {
          const button = node.querySelector('button').getBoundingClientRect();
          const caption = node.querySelector('.mud-typography-caption').getBoundingClientRect();
          const field = node.querySelector('input').getBoundingClientRect();
          return {buttonLeft: button.left, fieldLeft: field.left, buttonTop: button.top, captionBottom: caption.bottom,
            right: button.right, viewport: innerWidth};
        });
        assert(bounds.buttonTop >= bounds.captionBottom, 'Save action must be below its explanatory text');
        assert(bounds.right <= bounds.viewport, 'Save action must fit the viewport');
        const editorLeft = await editor.evaluate(node => node.getBoundingClientRect().left);
        assert(Math.abs(bounds.buttonLeft - editorLeft) <= 2, 'Save action must align with the fields container');
      }
      await page.setViewportSize({width: 1440, height: 1000});
    }
    const toggle = editor.getByRole('switch', {name: 'Override secondary report address', exact: true});
    originals.push({scope, address: await editor.getByLabel('Secondary DMARC report address', {exact: true}).inputValue(),
      override: scope === 'system' || await toggle.isChecked()});
    if (scope !== 'system') await toggle.check();
    await editor.getByLabel('Secondary DMARC report address', {exact: true}).fill(address);
    await save(editor);
    const reloaded = await editorFor(scope);
    assert.equal(await reloaded.getByLabel('Secondary DMARC report address', {exact: true}).inputValue(), address);
  }

  await page.goto(`${base}/domains/import`);
  const form = page.locator('#add-domain');
  await form.getByRole('button', {name: 'Add domain', exact: true}).waitFor({state: 'visible'});
  await page.waitForFunction(() => ![...document.querySelectorAll('#add-domain button')].find(button => button.textContent.trim() === 'Add domain')?.disabled);
  const domain = `report-destination-${Date.now()}.example`;
  await form.getByLabel('Domain name', {exact: true}).fill(domain);
  await form.getByLabel('Add to group', {exact: true}).fill('Aurora');
  await page.getByRole('option', {name: 'Aurora Retail', exact: true}).click();
  await form.getByRole('button', {name: 'Add domain', exact: true}).click();
  await page.waitForURL(`${base}/domains/${domain}?setup=true`);
  await reportEditorsReady();
  const domainEditor = page.locator('.dmarc-report-addresses');
  await page.locator('summary').filter({hasText: /^Report destinations$/}).click();
  await domainEditor.getByText('Group override: aurora-reports@example.com', {exact: true}).waitFor();
  assert.equal(await domainEditor.getByLabel('Secondary DMARC report address', {exact: true}).isDisabled(), true);

  await page.getByRole('button', {name: 'Change groups', exact: true}).click();
  await page.getByLabel('Domain groups', {exact: true}).click();
  await page.getByRole('checkbox', {name: 'Cobalt Freight', exact: true}).check();
  await page.keyboard.press('Escape');
  await page.getByRole('dialog').getByRole('button', {name: 'Save groups', exact: true}).click();
  await page.waitForFunction(() => document.querySelector('.domain-groups').textContent.includes('Cobalt Freight'));
  await page.reload();
  await reportEditorsReady();
  await page.locator('summary').filter({hasText: /^Report destinations$/}).click();
  await domainEditor.getByText(/Conflicting group report addresses/).waitFor();
  await page.getByRole('button', {name: 'Review DNS setup', exact: true}).click();
  await page.getByText('Groups specify conflicting secondary report addresses. Set a domain report-destination override before reviewing DMARC DNS.', {exact: true}).waitFor();
  assert.equal(await page.getByRole('dialog').count(), 0);

  await domainEditor.getByRole('switch', {name: 'Override secondary report address', exact: true}).check();
  await domainEditor.getByLabel('Secondary DMARC report address', {exact: true}).fill('mailto:invalid@example.com');
  await domainEditor.getByRole('button', {name: 'Save report destinations', exact: true}).click();
  await page.getByText('Enter one plain email address, without mailto:, spaces or separators.', {exact: false}).waitFor();
  await domainEditor.getByLabel('Secondary DMARC report address', {exact: true}).fill('');
  await save(domainEditor);
  await page.reload();
  await reportEditorsReady();
  await page.locator('summary').filter({hasText: /^Report destinations$/}).click();
  await domainEditor.getByText('Domain override: Primary mailbox only', {exact: true}).waitFor();
  assert.equal(await domainEditor.getByRole('switch', {name: 'Override secondary report address', exact: true}).isChecked(), true);
  await page.getByRole('button', {name: 'Review DNS setup', exact: true}).click();
  const dialog = page.getByRole('dialog');
  await dialog.waitFor();
  assert.match(await dialog.innerText(), /_dmarc\./);
  assert.doesNotMatch(await dialog.innerText(), /aurora-reports|cobalt-reports|system-reports/);
  assert.match(await dialog.innerText(), /SPF, DKIM keys and external report authorization require separate review/);
  await dialog.locator('.mud-dialog-actions').getByRole('button', {name: 'Done', exact: true}).click();
  assert.deepEqual(errors, []);
  console.log('Report-destination browser checks passed: read-only primary, persisted system/group overrides, group inheritance/conflicts, blocked DNS setup, address validation, explicit primary-only override and combined manual record review.');
} finally {
  for (const original of originals.reverse()) {
    const editor = await editorFor(original.scope);
    if (original.scope !== 'system') {
      await editor.getByRole('switch', {name: 'Override secondary report address', exact: true}).check();
    }
    await editor.getByLabel('Secondary DMARC report address', {exact: true}).fill(original.address);
    if (original.scope !== 'system' && !original.override) {
      await editor.getByRole('switch', {name: 'Override secondary report address', exact: true}).uncheck();
    }
    await save(editor);
  }
  await browser.close();
}
