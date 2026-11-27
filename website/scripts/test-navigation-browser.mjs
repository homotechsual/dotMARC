import assert from 'node:assert/strict';
import {chromium} from 'playwright';

const base = process.env.INVENTORY_TEST_URL ?? 'http://127.0.0.1:5187';
const browser = await chromium.launch({channel: 'msedge', headless: true});
const errors = [];
try {
  const page = await browser.newPage({viewport: {width: 1366, height: 768}});
  page.on('pageerror', error => errors.push(error.message));
  await page.request.post(`${base}/demo/sign-in/admin`);
  await page.goto(`${base}/dashboard`);
  await page.waitForFunction(() => !!JSON.parse(history.state?.userState ?? '{}').domainListEntry);
  const desktop = page.locator('.dotmarc-desktop-navigation');
  const navigation = page.getByRole('button', {name: 'Open navigation', exact: true});
  const menu = page.locator('.dotmarc-navigation-menu.mud-popover-open');
  assert(await desktop.isVisible());
  assert.equal(await navigation.isVisible(), false);
  await desktop.getByRole('button', {name: 'Manage', exact: true}).click();
  await page.getByRole('menuitem', {name: 'Baselines', exact: true}).waitFor();
  const management = await page.locator('.mud-popover-open a[href]').evaluateAll(links =>
    links.map(link => link.getAttribute('href')).sort());
  assert(management.includes('/groups') && management.includes('/baselines'));
  await page.getByRole('menuitem', {name: 'Groups', exact: true}).press('Escape');
  await page.locator('.mud-popover-open').waitFor({state: 'hidden'});

  for (const width of [1100, 1099, 1024, 768, 390, 320]) {
    await page.setViewportSize({width, height: 768});
    assert.equal(await desktop.isVisible(), width >= 1100);
    assert.equal(await navigation.isVisible(), width < 1100);
    const bounds = await page.locator('.dotmarc-appbar').evaluate(bar => ({
      scroll: bar.scrollWidth, client: bar.clientWidth,
      controls: [...bar.querySelectorAll('a,button')].filter(node => node.checkVisibility()).map(node => {
        const rect = node.getBoundingClientRect();
        return {left: rect.left, right: rect.right, top: rect.top, bottom: rect.bottom};
      }),
      top: bar.getBoundingClientRect().top, bottom: bar.getBoundingClientRect().bottom
    }));
    assert(bounds.scroll <= bounds.client + 1, `Navbar overflows at ${width}px`);
    assert(bounds.controls.every(control => control.left >= 0 && control.right <= width + 1
      && control.top >= bounds.top - 1 && control.bottom <= bounds.bottom + 1),
    `Navbar controls are clipped at ${width}px`);
    assert(await page.getByTitle('Toggle dark mode', {exact: true}).isVisible());
    assert(await page.getByTitle('Sign out', {exact: true}).isVisible());
    if (width >= 1100) continue;

    await navigation.click();
    await menu.waitFor();
    await menu.getByRole('menuitem', {name: 'Switch persona', exact: true}).waitFor();
    const links = await menu.locator('a[href]').evaluateAll(nodes => nodes.map(node => node.getAttribute('href')));
    assert.deepEqual(links.filter(href => !['/dashboard', '/domains', '/alerts', '/demo'].includes(href)).sort(), management);
    assert(links.includes('/dashboard') && links.includes('/domains') && links.includes('/alerts') && links.includes('/demo'));
    assert(await menu.evaluate(node => {
      const box = node.getBoundingClientRect();
      return box.left >= 0 && box.right <= innerWidth + 1 && box.top >= 0 && box.bottom <= innerHeight + 1;
    }), `Navigation menu is clipped at ${width}px`);
    assert(await menu.locator('.mud-menu-item').evaluateAll(items => items.every(item => item.getBoundingClientRect().height >= 44)));
    await menu.getByRole('menuitem', {name: 'Dashboard', exact: true}).press('Escape');
    await menu.waitFor({state: 'hidden'});
  }

  await navigation.focus();
  await navigation.press('Enter');
  await menu.waitFor();
  await menu.getByRole('menuitem', {name: 'Domains', exact: true}).click();
  await page.waitForURL(`${base}/domains`);
  await menu.waitFor({state: 'hidden'});
  await page.waitForFunction(() => !!JSON.parse(history.state?.userState ?? '{}').domainListEntry);
  await page.getByTitle('Toggle dark mode', {exact: true}).click();
  await page.waitForFunction(() => localStorage.getItem('dotmarc-dark-mode') === 'true');
  await navigation.click();
  await menu.getByRole('menuitem', {name: 'Groups', exact: true}).click();
  await page.waitForURL(`${base}/groups`);
  await menu.waitFor({state: 'hidden'});

  await page.setViewportSize({width: 390, height: 360});
  await navigation.click();
  await menu.waitFor();
  assert(await menu.evaluate(node => node.getBoundingClientRect().bottom <= innerHeight + 1));
  await page.evaluate(() => {
    const banner = document.createElement('div');
    banner.className = 'dev-build-banner';
    banner.dataset.navigationTest = 'true';
    banner.textContent = 'Dev build. This is unreleased dotMARC code.';
    document.body.prepend(banner);
  });
  assert(await page.locator('.dotmarc-appbar').evaluate(bar =>
    bar.getBoundingClientRect().top >= document.querySelector('.dev-build-banner').getBoundingClientRect().bottom - 1));
  await menu.getByRole('menuitem', {name: 'Dashboard', exact: true}).press('Escape');
  await menu.waitFor({state: 'hidden'});
  await navigation.click();
  await menu.waitFor();
  assert(await menu.evaluate(node => {
    const box = node.getBoundingClientRect();
    return box.top >= document.querySelector('.dotmarc-appbar').getBoundingClientRect().bottom - 1
      && box.bottom <= innerHeight + 1 && box.right <= innerWidth + 1;
  }), 'Navigation must stay below the development banner and app bar on short mobile screens');
  await menu.getByRole('menuitem', {name: 'Switch persona', exact: true}).scrollIntoViewIfNeeded();
  await page.locator('[data-navigation-test]').evaluate(node => node.remove());
  await menu.getByRole('menuitem', {name: 'Switch persona', exact: true}).click();
  await page.waitForURL(`${base}/demo`);
  await menu.waitFor({state: 'hidden'});

  await page.request.post(`${base}/demo/sign-in/viewer`);
  await page.goto(`${base}/dashboard`);
  await page.waitForFunction(() => !!JSON.parse(history.state?.userState ?? '{}').domainListEntry);
  await navigation.click();
  await menu.waitFor();
  const viewerLinks = await menu.locator('a[href]').evaluateAll(nodes => nodes.map(node => node.getAttribute('href')));
  assert(viewerLinks.includes('/dashboard') && viewerLinks.includes('/domains'));
  assert(viewerLinks.every(href => !management.includes(href)), 'Viewer must not gain management links through mobile navigation');
  assert.deepEqual(errors, []);
  console.log('Navigation browser checks passed: desktop/mobile breakpoints, unclipped navbar and menus, shared permission-gated destinations, touch targets, keyboard opening/Escape, route closure, dark mode, short screens and scoped Viewer.');
} finally {
  await browser.close();
}
