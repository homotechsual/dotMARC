import assert from 'node:assert/strict';
import {chromium} from 'playwright';

const base = process.env.INVENTORY_TEST_URL ?? 'http://127.0.0.1:5187';
const browser = await chromium.launch({channel: 'msedge', headless: true});
const page = await browser.newPage({viewport: {width: 1440, height: 1000}});
const errors = [];
page.on('pageerror', error => errors.push(error.message));
const ready = () => page.waitForFunction(() => !!JSON.parse(history.state?.userState ?? '{}').domainListEntry);
const select = name => page.getByRole('checkbox', {name: `Select ${name}`, exact: true}).first().check();
const review = async provider => {
  await page.getByRole('button', {name: 'Review selected DNS', exact: true}).click();
  await page.getByRole('dialog').getByRole('button', {name: `Review ${provider}`, exact: true}).click();
  await page.waitForURL(url => url.pathname.startsWith('/dns-push/batches/'));
  await page.getByText('DNS batch review and results', {exact: true}).waitFor();
  await page.waitForFunction(() => [...document.querySelectorAll('button')].some(button =>
    button.textContent.trim() === 'Authorize and push checked domains' && !button.disabled));
};
const push = async () => {
  const popup = page.waitForEvent('popup');
  await page.getByRole('button', {name: 'Authorize and push checked domains', exact: true}).click();
  await popup;
  await page.waitForFunction(() => document.querySelector('.mud-main-content').innerText.includes('Completed'));
};
try {
  await page.request.post(`${base}/demo/sign-in/admin`);
  await page.goto(`${base}/domains?group=all&filters=1&open=true`);
  await ready();
  await select('aurora-retail.example');
  await select('shop.aurora-retail.example');
  await page.getByRole('button', {name: 'By group', exact: true}).click();
  await page.getByText('2 selected domains', {exact: true}).waitFor();
  await page.getByRole('button', {name: 'All domains', exact: true}).click();
  await page.getByLabel('Domain search', {exact: true}).fill('shop.aurora');
  await page.getByText('1 selected domains', {exact: true}).waitFor();
  await page.getByRole('button', {name: 'Clear filters', exact: true}).click();
  assert.equal(await page.getByLabel('Domain search', {exact: true}).inputValue(), 'shop.aurora');
  await page.getByLabel('Domain search', {exact: true}).fill('');
  await select('aurora-retail.example');
  await page.getByRole('button', {name: 'Recheck selected providers', exact: true}).click();
  await page.getByText(/Provider checks: 2 succeeded, 0 failed, 0 not attempted/).waitFor();
  await page.locator('.domain-inventory').last().scrollIntoViewIfNeeded();
  const selectedToolbar = page.locator('.domain-dns-toolbar').filter({has: page.getByRole('button', {name: 'Review selected DNS', exact: true})});
  assert.equal(await selectedToolbar.count(), 1);
  const sticky = await selectedToolbar.evaluate(node => ({
    top: node.getBoundingClientRect().top,
    expected: parseFloat(getComputedStyle(node).top),
    position: getComputedStyle(node).position
  }));
  assert.equal(sticky.position, 'sticky');
  assert(Math.abs(sticky.top - sticky.expected) <= 1);
  await select('brightline-legal.example');
  await page.getByRole('button', {name: 'Review selected DNS', exact: true}).click();
  await page.getByRole('dialog').getByText(/Set up manually:/).waitFor();
  await page.getByRole('dialog').getByRole('button', {name: 'Cancel', exact: true}).click();
  await review('cloudflare');
  const firstBatch = page.url();
  const rows = page.locator('.dns-batch-domain');
  await rows.filter({hasText: 'aurora-retail.example'}).first().waitFor();
  assert.equal(await page.getByText('Already correct', {exact: true}).count(), 1);
  const shop = rows.filter({has: page.getByText('shop.aurora-retail.example', {exact: true})});
  await shop.locator('summary').click();
  await shop.getByText('_dmarc.shop.aurora-retail.example', {exact: true}).first().waitFor();
  await push();
  await page.getByText('Pushed', {exact: true}).waitFor();
  await page.reload();
  await page.getByText('Pushed', {exact: true}).waitFor();

  await page.goto(`${base}/domains?group=all`);
  await ready();
  await select('cobalt-freight.example');
  await select('fleet.cobalt-freight.example');
  await review('azure-dns');
  await push();
  await page.getByText('Pushed', {exact: true}).waitFor();
  await page.getByText('Failed', {exact: true}).waitFor();
  await page.getByText('The demo provider rejected this record. No real DNS was changed.', {exact: true}).waitFor();
  const finishedBatch = page.url();
  await page.getByRole('button', {name: 'Create fresh review for unpushed domains', exact: true}).click();
  await page.waitForURL(url => url.toString() !== finishedBatch);
  await page.waitForFunction(() => document.querySelectorAll('.dns-batch-domain').length === 1);
  assert.equal(await page.locator('.dns-batch-domain').count(), 1);
  await page.getByRole('button', {name: 'Authorize and push checked domains', exact: true}).waitFor();
  await page.setViewportSize({width: 390, height: 844});
  const bounds = await page.locator('.dns-batch-domain').first().evaluate(node => {
    const box = node.getBoundingClientRect();
    return {left: box.left, right: box.right, width: innerWidth};
  });
  assert(bounds.left >= 0 && bounds.right <= bounds.width);

  await page.request.post(`${base}/demo/sign-in/viewer`);
  await page.goto(`${base}/domains`);
  await ready();
  assert.equal(await page.getByRole('button', {name: 'Review selected DNS', exact: true}).count(), 0);
  assert.equal(await page.getByRole('checkbox', {name: /^Select .+\.example$/}).count(), 0);
  assert.equal(await page.getByRole('button', {name: 'Recheck selected providers', exact: true}).count(), 0);
  await page.goto(firstBatch);
  await page.getByText('That DNS batch is unavailable or belongs to another person.', {exact: false}).waitFor();
  assert.equal(await page.getByRole('button', {name: 'Authorize and push checked domains', exact: true}).count(), 0);
  assert.deepEqual(errors, []);
  console.log('Bulk DNS browser checks passed: shared selection, pruning hidden IDs, provider/manual grouping, stored review, demo popup sign-in, success/already-correct/partial failure, persisted results, fresh retry, mobile cards and viewer isolation.');
} finally {
  await browser.close();
}
