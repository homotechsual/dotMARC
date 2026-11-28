import assert from 'node:assert/strict';
import {chromium} from 'playwright';

const base = process.env.INVENTORY_TEST_URL ?? 'http://127.0.0.1:5187';
const browser = await chromium.launch({channel: 'msedge', headless: true});
try {
  const page = await browser.newPage({viewport: {width: 1366, height: 1000}});
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.request.post(`${base}/demo/sign-in/admin`);
  await page.goto(`${base}/domains?group=all`);
  await page.waitForFunction(() => !!JSON.parse(history.state?.userState ?? '{}').domainListEntry);
  const allNames = [...new Set(await page.locator('a[data-domain-link]').allTextContents())];
  assert(allNames.length > 2);
  await page.getByRole('button', {name: 'Select all', exact: true}).click();
  const selected = count => page.getByText(new RegExp(`^${count} selected (domains|for DNS review)$`));
  await selected(allNames.length).waitFor();
  await page.getByLabel('Domain search', {exact: true}).fill('shop.aurora');
  await page.waitForFunction(() => document.querySelectorAll('a[data-domain-link]').length === 1);
  await selected(allNames.length).waitFor();
  await page.getByText(`${allNames.length - 1} selected domains hidden by filters; actions include them.`, {exact: true}).waitFor();
  await page.getByRole('button', {name: 'Review selected DNS', exact: true}).click();
  await page.getByRole('dialog').getByText(/aurora-retail.example/).first().waitFor();
  assert((await page.getByRole('dialog').innerText()).includes('cobalt-freight.example'),
    'DNS review must include deliberately selected hidden domains');
  await page.getByRole('dialog').getByRole('button', {name: 'Cancel', exact: true}).click();
  await page.getByRole('button', {name: 'Select domains', exact: true}).click();
  await page.getByRole('menuitem', {name: 'Select visible (1)', exact: true}).click();
  await selected(1).waitFor();
  await page.getByLabel('Domain search', {exact: true}).fill('no-domain-matches-this');
  await selected(0).waitFor();
  await page.getByRole('button', {name: 'Select domains', exact: true}).click();
  assert(await page.getByRole('menuitem', {name: 'Select visible (0)', exact: true}).getAttribute('aria-disabled') === 'true');
  await page.getByRole('menuitem', {name: `Select all (${allNames.length} accessible)`, exact: true}).click();
  await selected(allNames.length).waitFor();
  await page.getByRole('button', {name: 'Clear selection', exact: true}).click();
  await page.getByLabel('Domain search', {exact: true}).fill('');
  await page.getByRole('button', {name: 'By group', exact: true}).click();
  await page.getByRole('button', {name: 'Select all', exact: true}).click();
  await selected(allNames.length).waitFor();
  await page.getByRole('button', {name: 'Clear selection', exact: true}).click();

  for (const path of ['/alerts', '/logs/audit']) {
    await page.goto(`${base}${path}`);
    await page.waitForFunction(() => document.querySelector('.list-toolbar')?.dataset.interactive === 'true');
    const table = page.locator('.responsive-detail-table').first();
    await table.locator('tbody tr').first().waitFor();
    // Exercise unbroken values as well as the seeded content, without changing stored data.
    await table.locator('tbody tr').first().locator('td').last().evaluate(node => {
      node.appendChild(document.createTextNode(' long-value-' + 'x'.repeat(500)));
    });
    for (const width of [1920, 1366, 1280, 1100, 1024, 768, 390, 320]) {
      await page.setViewportSize({width, height: 1000});
      const overflow = await page.evaluate(() => {
        const nodes = [document.documentElement, ...document.querySelectorAll('.responsive-detail-table .mud-table-container, .responsive-detail-table table')];
        return nodes.filter(node => node.scrollWidth > node.clientWidth + 1).map(node => ({
          name: node.className, scroll: node.scrollWidth, client: node.clientWidth
        }));
      });
      assert.deepEqual(overflow, [], `${path} must not scroll horizontally at ${width}px`);
      if (width < 1280) {
        assert(await table.locator('tbody tr').first().locator('td').first().evaluate(node => getComputedStyle(node).display === 'block'));
      }
    }
  }
  assert.deepEqual(errors, []);
  console.log('Selection and table browser checks passed: all/visible, hidden-domain DNS review, empty results, grouped deduplication, Alerts/Audit without horizontal scrolling at 1920/1366/1280/1100/1024/768/390/320px.');
} finally {
  await browser.close();
}
