import assert from 'node:assert/strict';
import {chromium} from 'playwright';

const base = process.env.INVENTORY_TEST_URL ?? 'http://127.0.0.1:5187';
const browser = await chromium.launch({channel: 'msedge', headless: true});
const page = await browser.newPage({viewport: {width: 1280, height: 1000}});
const errors = [];
page.on('pageerror', error => errors.push(error.message));
const configure = async () => {
  await page.getByRole('button', {name: 'Configure selectors', exact: true}).click();
  await page.getByRole('dialog').getByText('Observed in retained DMARC reports', {exact: true}).waitFor();
};
try {
  await page.request.post(`${base}/demo/sign-in/admin`);
  await page.goto(`${base}/domains/aurora-retail.example`);
  await page.waitForFunction(() => !!document.querySelector('[data-permanent]') ||
    [...document.querySelectorAll('button')].some(button => button.textContent.includes('Configure selectors')));
  await configure();
  const dialog = page.getByRole('dialog');
  const selectors = dialog.getByLabel('Selectors', {exact: true});
  const original = await selectors.inputValue();
  const add = dialog.getByRole('button', {name: 'Add observed selector selector1', exact: true});
  await add.waitFor();
  await dialog.getByText(/\d[\d,]* messages .*Last seen .* UTC/).first().waitFor();
  await selectors.fill('manual-only');
  await add.click();
  await page.waitForFunction(() => document.querySelector('textarea').value.includes('selector1'));
  assert.equal(await selectors.inputValue(), 'manual-only\nselector1');
  assert.equal(await add.isDisabled(), true);
  await dialog.getByRole('button', {name: 'Cancel', exact: true}).click();
  await configure();
  assert.equal(await selectors.inputValue(), original);
  await dialog.getByRole('button', {name: 'Cancel', exact: true}).click();

  await page.goto(`${base}/domains/shop.aurora-retail.example`);
  await configure();
  await dialog.getByText('No usable selectors observed signing as this domain. You can still enter selectors manually.', {exact: true}).waitFor();
  assert.equal(await dialog.getByRole('button', {name: /^Add observed selector /}).count(), 0);
  await dialog.getByRole('button', {name: 'Cancel', exact: true}).click();
  assert.deepEqual(errors, []);
  console.log('DKIM suggestion browser checks passed: message/last-seen evidence, explicit additive draft, duplicate prevention, cancellation preserves manual configuration, and empty evidence.');
} finally {
  await browser.close();
}
