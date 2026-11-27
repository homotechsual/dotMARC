import assert from 'node:assert/strict';
import {chromium} from 'playwright';

const base = process.env.INVENTORY_TEST_URL ?? 'http://127.0.0.1:5187';
const domain = 'shop.aurora-retail.example';
const browser = await chromium.launch({channel: 'msedge', headless: true});
const errors = [];
try {
  const page = await browser.newPage({viewport: {width: 1440, height: 1000}});
  page.on('pageerror', error => errors.push(error.message));
  const enter = async () => {
    await page.goto(`${base}/domains?search=${domain}`);
    await page.waitForFunction(() => !!JSON.parse(history.state?.userState ?? '{}').domainListEntry);
    await page.locator('a[data-domain-link]').filter({hasText: domain}).click();
    await page.getByRole('button', {name: 'Change groups', exact: true}).waitFor({state: 'visible'});
    await page.waitForFunction(() => ![...document.querySelectorAll('button')].find(button => button.textContent.trim() === 'Change groups')?.disabled);
  };
  await page.request.post(`${base}/demo/sign-in/admin`);
  await enter();
  const groups = page.locator('.domain-groups');
  const originalGroups = await groups.locator('.mud-typography-body2').innerText();
  const change = async checked => {
    await page.getByRole('button', {name: 'Change groups', exact: true}).click();
    const dialog = page.getByRole('dialog');
    await dialog.waitFor();
    await page.getByLabel('Domain groups', {exact: true}).click();
    await page.getByRole('checkbox', {name: 'Cobalt Freight', exact: true}).setChecked(checked);
    await page.keyboard.press('Escape');
    await dialog.getByRole('button', {name: 'Save groups', exact: true}).click();
    await dialog.waitFor({state: 'hidden'});
  };
  assert(!originalGroups.includes('Cobalt Freight'));
  await change(true);
  await page.waitForFunction(() => document.querySelector('.domain-groups').textContent.includes('Cobalt Freight'));
  await page.reload();
  await page.getByRole('button', {name: 'Change groups', exact: true}).waitFor();
  assert((await groups.innerText()).includes('Cobalt Freight'));
  await change(false);
  await page.waitForFunction(original => document.querySelector('.domain-groups .mud-typography-body2').textContent === original, originalGroups);

  const review = page.getByRole('link', {name: `Review sender decisions for ${domain}`, exact: true});
  await review.waitFor();
  assert.equal(await review.getAttribute('href'), `/domains/${domain}/sources`);
  await review.click();
  await page.waitForURL(target => target.pathname.endsWith('/sources'));
  const toggle = page.getByRole('button', {name: /^Show source IPs and evidence for /}).first();
  await toggle.click();
  assert.equal(await page.getByRole('button', {name: /^Hide source IPs and evidence for /}).first().getAttribute('aria-expanded'), 'true');
  while (await page.getByRole('button', {name: /^Show source IPs and evidence for /}).count()) {
    await page.getByRole('button', {name: /^Show source IPs and evidence for /}).first().click();
  }
  const decideIp = page.getByRole('button', {name: /^Decide on source IP /}).first();
  await decideIp.waitFor();
  const ip = (await decideIp.getAttribute('aria-label')).replace('Decide on source IP ', '');
  await decideIp.click();
  const dialog = page.getByRole('dialog');
  await dialog.waitFor();
  await dialog.getByRole('radio', {name: /^Under review/}).check();
  await dialog.getByRole('button', {name: 'Save', exact: true}).click();
  await dialog.waitFor({state: 'hidden'});
  const changeIp = page.getByRole('button', {name: `Change decision for source IP ${ip}`, exact: true});
  await changeIp.waitFor();
  await changeIp.click();
  await dialog.waitFor();
  await dialog.getByRole('button', {name: 'Reset to inherited', exact: true}).click();
  await dialog.waitFor({state: 'hidden'});
  await page.getByRole('button', {name: `Decide on source IP ${ip}`, exact: true}).waitFor();

  await page.setViewportSize({width: 390, height: 844});
  await page.getByRole('button', {name: `Decide on source IP ${ip}`, exact: true}).click();
  await dialog.waitFor();
  await dialog.getByRole('button', {name: 'Cancel', exact: true}).click();
  await dialog.waitFor({state: 'hidden'});
  await page.goto(`${base}/domains/${domain}`);
  await page.getByRole('link', {name: `Review sender decisions for ${domain}`, exact: true}).waitFor();
  await page.waitForFunction(() => [...document.querySelectorAll('button')].some(button => button.textContent.trim() === 'Change groups' && !button.disabled));
  const geometry = await page.locator('.domain-sender-review').evaluate(node => {
    const card = node.getBoundingClientRect();
    const button = node.querySelector('a').getBoundingClientRect();
    return {cardLeft: card.left, cardRight: card.right, buttonLeft: button.left, buttonRight: button.right, viewport: innerWidth};
  });
  assert(geometry.buttonLeft >= geometry.cardLeft && geometry.buttonRight <= geometry.cardRight);
  assert(geometry.cardLeft >= 0 && geometry.cardRight <= geometry.viewport);
  if (process.env.OVERVIEW_SCREENSHOT) await page.screenshot({path: process.env.OVERVIEW_SCREENSHOT, fullPage: true});

  await page.request.post(`${base}/demo/sign-in/viewer`);
  await page.goto(`${base}/domains/${domain}`);
  await page.getByRole('link', {name: `View sender evidence for ${domain}`, exact: true}).waitFor();
  assert.equal(await page.getByRole('button', {name: 'Change groups', exact: true}).count(), 0);
  await page.getByRole('link', {name: `View sender evidence for ${domain}`, exact: true}).click();
  await page.waitForURL(target => target.pathname.endsWith('/sources'));
  await page.getByText('You do not have permission to change decisions.', {exact: false}).waitFor();
  await page.getByRole('button', {name: /^Show source IPs and evidence for /}).first().click();
  assert.equal(await page.getByRole('button', {name: /^(Decide on|Change decision for) (service|source IP) /}).count(), 0);
  assert.deepEqual(errors, []);
  console.log('Domain Overview browser checks passed: group save/reload/restore, Review link, source-IP decisions/reset, mobile alignment and read-only viewer.');
} finally {
  await browser.close();
}
