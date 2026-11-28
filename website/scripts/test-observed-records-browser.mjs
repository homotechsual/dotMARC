import assert from 'node:assert/strict';
import {chromium} from 'playwright';

const base = process.env.INVENTORY_TEST_URL ?? 'http://127.0.0.1:5196';
const browser = await chromium.launch({channel: 'msedge', headless: true});
try {
  const page = await browser.newPage({viewport: {width: 1366, height: 1000}});
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.request.post(`${base}/demo/sign-in/admin`);
  await page.goto(`${base}/domains/cobalt-freight.example`);
  const section = page.locator('.domain-observed-records');
  await page.waitForFunction(() => [...document.querySelectorAll('.domain-observed-records button')].some(button => !button.disabled));
  await section.locator('summary').click();
  const compliance = page.locator('.dotmarc-health-row').filter({hasText: 'DMARC target compliance'});
  assert.equal(await compliance.count(), 1);
  const before = await page.locator('.dotmarc-health-row').allTextContents();
  await section.getByRole('button', {name: 'Load current records', exact: true}).click();
  await page.waitForFunction(() => document.querySelectorAll('.domain-observed-records tbody tr').length >= 8
    && ![...document.querySelectorAll('.domain-observed-records button')].some(button => button.disabled), null, {timeout: 180000});
  const body = await section.innerText();
  for (const label of ['DMARC', 'SPF / apex TXT', 'MX', 'DKIM', 'TLS reporting', 'MTA-STS policy ID', 'MTA-STS hosting', 'Nameservers'])
    assert(body.includes(label), `Missing observed ${label}`);
  assert(body.includes('Lookup started:'));
  assert(body.includes('does not change DNS or update the saved health statuses'));
  assert.deepEqual(await page.locator('.dotmarc-health-row').allTextContents(), before, 'Read-only lookup must not alter saved statuses/timestamps');
  await section.locator('tbody tr').first().locator('td').last().evaluate(node => {
    const value = document.createElement('pre');
    value.className = 'dns-record-value';
    value.textContent = 'v=DKIM1; p=' + 'x'.repeat(1000);
    node.append(value);
  });
  for (const width of [1366, 1024, 768, 390, 320]) {
    await page.setViewportSize({width, height: 1000});
    assert(await section.evaluate(node => node.scrollWidth <= node.clientWidth + 1), `Record section overflows at ${width}px`);
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `Domain page overflows at ${width}px`);
  }
  assert.deepEqual(errors, []);
  console.log('Observed records browser checks passed: all record categories, separate target compliance, read-only health status, explicit timing and 1366/1024/768/390/320px layouts.');
} finally {
  await browser.close();
}
