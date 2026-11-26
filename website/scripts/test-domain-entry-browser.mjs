import assert from 'node:assert/strict';
import {chromium} from 'playwright';

const base = process.env.INVENTORY_TEST_URL ?? 'http://127.0.0.1:5187';
const browser = await chromium.launch({channel: 'msedge', headless: true});
const errors = [];
try {
  const page = await browser.newPage({viewport: {width: 1440, height: 1000}});
  page.on('pageerror', error => errors.push(error.message));
  await page.request.post(`${base}/demo/sign-in/admin`);
  const ready = async () => {
    await page.waitForFunction(() => [...document.querySelectorAll('#add-domain button')].some(button =>
      button.textContent.trim() === 'Add domain' && !button.disabled));
  };
  await page.goto(`${base}/dashboard?filters=1&open=true`);
  await ready();
  await page.getByLabel('Group', {exact: true}).fill('Aurora');
  await page.getByRole('option', {name: 'Aurora Retail', exact: true}).click();
  await page.waitForURL(url => url.searchParams.has('group'));
  const group = new URL(page.url()).searchParams.get('group');
  const importHref = `/domains/import?group=${group}`;
  await page.waitForFunction(href => [...document.querySelectorAll('a')].some(link =>
    link.textContent.trim() === 'Import domains' && link.getAttribute('href') === href), importHref);
  assert.equal(await page.getByRole('link', {name: 'Import domains', exact: true}).getAttribute('href'), importHref);

  const stamp = Date.now();
  for (const [index, path] of ['/dashboard', '/domains', '/domains/import'].entries()) {
    await page.goto(`${base}${path}?group=${group}`);
    await ready();
    const form = page.locator('#add-domain');
    assert.equal(await form.count(), 1);
    assert.equal(await form.locator('details').getAttribute('open'), null);
    await form.getByRole('checkbox', {name: 'Open domain and set up DNS', exact: true}).uncheck();
    assert.equal(await form.getByLabel('Add to group', {exact: true}).inputValue(), 'Aurora Retail');
    const expectedGroup = index === 1 ? 'Cobalt Freight' : 'Aurora Retail';
    if (index === 1) {
      await form.getByLabel('Add to group', {exact: true}).fill('Cobalt');
      await page.getByRole('option', {name: expectedGroup, exact: true}).click();
      assert.equal(new URL(page.url()).searchParams.get('group'), group);
    }
    await form.getByLabel('Domain name', {exact: true}).fill('not a domain');
    await form.getByRole('button', {name: 'Add domain', exact: true}).click();
    await form.getByText('Enter a valid domain name.', {exact: true}).waitFor();
    const name = `entry-${stamp}-${index}.example`;
    await form.getByLabel('Domain name', {exact: true}).fill(name.toUpperCase());
    await form.getByRole('button', {name: 'Add domain', exact: true}).dblclick();
    await form.getByRole('link', {name: 'View domain', exact: true}).waitFor();
    assert.equal(new URL(page.url()).pathname, path);
    assert.equal(await form.getByRole('link', {name: 'View domain', exact: true}).getAttribute('href'), `/domains/${name}`);
    await form.getByLabel('Domain name', {exact: true}).fill(name);
    await form.getByRole('button', {name: 'Add domain', exact: true}).click();
    await form.getByText('That domain already exists.', {exact: true}).waitFor();
    await page.goto(`${base}/domains/${name}`);
    await page.locator('.domain-groups .mud-typography-body2').waitFor();
    assert.equal(await page.locator('.domain-groups .mud-typography-body2').innerText(), expectedGroup);
    await page.reload();
    await page.locator('.domain-groups .mud-typography-body2').waitFor();
    assert.equal(await page.locator('.domain-groups .mud-typography-body2').innerText(), expectedGroup);
  }

  await page.goto(`${base}/domains/import?group=${group}`);
  await ready();
  const advancedForm = page.locator('#add-domain');
  assert.equal(await advancedForm.getByRole('checkbox', {name: 'Open domain and set up DNS', exact: true}).isChecked(), true);
  const fieldBounds = await advancedForm.locator('.domain-entry-fields').evaluate(node => {
    const inputs = [...node.querySelectorAll('input')].map(input => input.getBoundingClientRect());
    const button = node.querySelector('button.mud-button-root').getBoundingClientRect();
    return {delta: Math.abs(inputs[0].top - inputs[1].top), buttonTop: button.top, inputTop: inputs[0].top};
  });
  assert(fieldBounds.delta <= 2, `Domain/group input misalignment: ${fieldBounds.delta}px`);
  assert(Math.abs(fieldBounds.buttonTop - fieldBounds.inputTop) <= 16, 'Add button must align with the inputs');
  await advancedForm.locator('summary').click();
  await advancedForm.getByLabel('Policy (p)', {exact: true}).click();
  await page.getByRole('option', {name: 'Reject', exact: true}).click();
  await advancedForm.getByLabel('Subdomain policy (sp)', {exact: true}).click();
  await page.getByRole('option', {name: 'Quarantine', exact: true}).click();
  await advancedForm.getByRole('switch', {name: 'Monitor this domain', exact: true}).uncheck();
  await advancedForm.getByLabel('DKIM selectors', {exact: true}).fill('selector1\nselector2');
  await advancedForm.getByRole('switch', {name: 'Enable MTA-STS hosting', exact: true}).check();
  const advancedName = `advanced-entry-${stamp}.example`;
  await advancedForm.getByLabel('Domain name', {exact: true}).fill(advancedName);
  await advancedForm.getByRole('button', {name: 'Add domain', exact: true}).click();
  await advancedForm.getByText('Check DKIM selectors and MTA-STS settings. Enabled MTA-STS needs valid MX hosts and a max age from 1 to 31557600.', {exact: true}).waitFor();
  await advancedForm.getByLabel('MTA-STS MX hosts', {exact: true}).fill('*.mail.example.com');
  await advancedForm.getByLabel('MTA-STS max age (seconds)', {exact: true}).fill('86400');
  await advancedForm.getByRole('button', {name: 'Add domain', exact: true}).click();
  await page.waitForURL(`${base}/domains/${advancedName}?setup=true`);
  await page.locator('.dotmarc-health-row').filter({has: page.getByText('DNS provider', {exact: true})}).getByText(/^Checked:/).waitFor();
  await page.getByText('Next: set up DNS', {exact: true}).waitFor();
  await page.getByRole('button', {name: 'Review DNS setup', exact: true}).waitFor();
  await page.reload();
  await page.getByText('Next: set up DNS', {exact: true}).waitFor();
  assert.match(await page.locator('body').innerText(), /p=reject; sp=quarantine/);
  await page.getByRole('button', {name: 'Configure selectors', exact: true}).click();
  await page.getByRole('dialog').getByLabel('Selectors', {exact: true}).waitFor();
  assert.equal(await page.getByRole('dialog').getByLabel('Selectors', {exact: true}).inputValue(), 'selector1\nselector2');
  await page.getByRole('dialog').getByRole('button', {name: 'Cancel', exact: true}).click();

  await page.goto(`${base}/domains/import?group=2147483647`);
  await ready();
  await page.getByLabel('Domain name', {exact: true}).fill(`invalid-group-${stamp}.example`);
  await page.getByRole('button', {name: 'Add domain', exact: true}).click();
  await page.getByText('The selected group no longer exists. Reload the page and choose a group.', {exact: true}).waitFor();
  assert.equal(await page.getByRole('link', {name: 'View domain', exact: true}).count(), 0);

  await page.goto(`${base}/domains?group=${group}#add-domain`);
  await ready();
  await page.waitForFunction(() => document.activeElement === document.querySelector('#add-domain input'));
  await page.getByRole('link', {name: 'Import domains', exact: true}).click();
  await page.waitForURL(`${base}${importHref}`);
  await ready();
  await page.getByLabel('Domains', {exact: true}).fill(`bulk-preview-${stamp}.example`);
  await page.getByRole('button', {name: 'Preview', exact: true}).click();
  await page.getByRole('button', {name: 'Import: add 1, update 0', exact: true}).waitFor();
  assert.equal(await page.locator('#add-domain').count(), 0);
  await page.getByRole('button', {name: 'Back', exact: true}).click();
  await ready();
  assert.equal(await page.getByLabel('Domains', {exact: true}).inputValue(), `bulk-preview-${stamp}.example`);
  await page.setViewportSize({width: 390, height: 844});
  const geometry = await page.locator('#add-domain').evaluate(node => {
    const bounds = node.getBoundingClientRect();
    const controls = [...node.querySelectorAll('input,button')].filter(element => element.getClientRects().length).map(element => element.getBoundingClientRect());
    return {left: bounds.left, right: bounds.right, viewport: innerWidth,
      contained: controls.every(control => control.left >= bounds.left && control.right <= bounds.right)};
  });
  assert(geometry.left >= 0 && geometry.right <= geometry.viewport && geometry.contained);
  if (process.env.ENTRY_SCREENSHOT) await page.screenshot({path: process.env.ENTRY_SCREENSHOT, fullPage: true});
  await page.getByRole('button', {name: 'Preview', exact: true}).click();
  await page.getByRole('button', {name: 'Import: add 1, update 0', exact: true}).click();
  await page.getByText(/Added 1, updated 0/).waitFor();
  await page.waitForFunction(() => [...document.querySelectorAll('button')].some(button =>
    button.textContent.trim() === 'Import more' && !button.disabled));
  assert.equal(await page.locator('#add-domain').count(), 0);
  await page.goto(`${base}/domains/bulk-preview-${stamp}.example`);
  await page.locator('.domain-groups .mud-typography-body2').waitFor();
  assert.equal(await page.locator('.domain-groups .mud-typography-body2').innerText(), 'No groups assigned in your scope.');
  await page.locator('.dotmarc-health-row').filter({has: page.getByText('DNS provider', {exact: true})}).getByText(/^Checked:/).waitFor();

  await page.request.post(`${base}/demo/sign-in/viewer`);
  for (const path of ['/dashboard', '/domains']) {
    await page.goto(`${base}${path}`);
    await page.waitForFunction(() => !!JSON.parse(history.state?.userState ?? '{}').domainListEntry);
    assert.equal(await page.locator('#add-domain').count(), 0);
    assert.equal(await page.getByRole('link', {name: 'Import domains', exact: true}).count(), 0);
  }
  await page.goto(`${base}/domains/import`);
  assert.equal(await page.locator('#add-domain').count(), 0);
  assert.deepEqual(errors, []);
  console.log('Domain-entry browser checks passed: all three entry pages, aligned fields, collapsed Advanced, policy/DKIM/MTA-STS settings, default DNS-setup navigation, optional stay-on-page, validation, group persistence, bulk isolation, mobile controls and read-only access.');
} finally {
  await browser.close();
}
