#!/usr/bin/env node

/**
 * Captures the screenshots the docs, homepage and release posts use, from a running dotMARC demo instance, in both
 * the light and dark themes, so they can be refreshed by running one command instead of by hand.
 *
 * Start the app in demo mode first (its data comes from src/DotMarc/Demo and is seeded from the UTC date, so a
 * given day always produces the same demo data):
 *
 *   Demo__Enabled=true ASPNETCORE_URLS=http://localhost:5195 dotnet run --project src/DotMarc
 *
 * Then:
 *
 *   node website/scripts/capture-screenshots.mjs
 *
 * Options:
 *   --base-url <url>    where the demo is running (default http://localhost:5195)
 *   --out <dir>         where the PNGs go (default website/static/img/screenshots)
 *   --domain <name>     the domain shown on the domain pages (default cobalt-freight.example)
 *   --max-height <px>   cut very tall pages off at this height (default 1500), so a list of many rows doesn't
 *                       produce an image far taller than it is wide
 *   --only <a,b>        capture just these names, for example dashboard-admin,sources-tab
 *   --channel <name>    use an installed browser instead of Playwright's Chromium, for example msedge or chrome
 *
 * Playwright's own browser is installed with `npx playwright install chromium`; on a machine that already has
 * Edge, `--channel msedge` needs nothing extra.
 */

import {mkdirSync} from 'node:fs';
import {dirname, join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {chromium} from 'playwright';

const scriptDirectory = dirname(fileURLToPath(import.meta.url));

function option(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index > -1 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const baseUrl = option('base-url', 'http://localhost:5195').replace(/\/$/, '');
const outputDirectory = option('out', join(scriptDirectory, '..', 'static', 'img', 'screenshots'));
const domain = option('domain', 'cobalt-freight.example');
const channel = option('channel', undefined);
const maxHeight = Number(option('max-height', '1500'));
const only = option('only', undefined)?.split(',').map((name) => name.trim());

// The demo has two people to sign in as. `user` picks which.
const shots = [
  {name: 'dashboard-admin', user: 'Admin', path: '/dashboard'},
  {name: 'dashboard-viewer-scoped', user: 'Viewer', path: '/dashboard'},
  {name: 'dns-push-settings', user: 'Admin', path: '/dns-push/settings'},
  {name: 'domain-detail', user: 'Admin', path: `/domains/${domain}`},
  {name: 'sources-tab', user: 'Admin', path: `/domains/${domain}/sources`},
  {name: 'manage-access', user: 'Admin', path: '/access'},
  {name: 'manage-domains', user: 'Admin', path: '/domains'},
  {name: 'manage-groups', user: 'Admin', path: '/groups'},
].filter((shot) => !only || only.includes(shot.name));

if (shots.length === 0) {
  console.error(`[screenshots] Nothing to capture: --only ${only?.join(',')} matched none of the known names.`);
  process.exit(1);
}

mkdirSync(outputDirectory, {recursive: true});

const browser = await chromium.launch({channel});

async function signedInPage(user, dark) {
  const context = await browser.newContext({viewport: {width: 1440, height: 900}, deviceScaleFactor: 1});
  // The app remembers the theme in localStorage, so set it before anything loads.
  await context.addInitScript((isDark) => localStorage.setItem('dotmarc-dark-mode', isDark ? 'true' : 'false'), dark);
  const page = await context.newPage();
  await page.goto(`${baseUrl}/demo`);
  await page.getByRole('button', {name: `Continue as Demo ${user}`}).click();
  await page.waitForURL('**/dashboard');
  return {context, page};
}

async function settle(page) {
  // Blazor Server renders in two steps (prerender, then the live circuit), and the charts animate in, so wait for
  // the network to go quiet and any spinner to leave before taking the picture.
  await page.waitForLoadState('networkidle');
  await page.locator('.mud-progress-circular, .mud-progress-linear').first().waitFor({state: 'detached', timeout: 15000}).catch(() => {});
  await page.waitForTimeout(1500);
}

let captured = 0;
try {
  for (const dark of [false, true]) {
    for (const user of ['Admin', 'Viewer']) {
      const wanted = shots.filter((shot) => shot.user === user);
      if (wanted.length === 0) {
        continue;
      }

      const {context, page} = await signedInPage(user, dark);
      for (const shot of wanted) {
        const response = await page.goto(`${baseUrl}${shot.path}`);
        if (!response || !response.ok()) {
          throw new Error(`${shot.path} answered ${response?.status() ?? 'nothing'}. Is the demo running at ${baseUrl}?`);
        }
        await settle(page);

        const file = join(outputDirectory, `${shot.name}${dark ? '-dark' : ''}.png`);
        const pageHeight = await page.evaluate(() => document.documentElement.scrollHeight);
        await page.screenshot({path: file, fullPage: true, clip: {x: 0, y: 0, width: 1440, height: Math.min(pageHeight, maxHeight)}});
        captured++;
        console.log(`[screenshots] ${shot.name}${dark ? '-dark' : ''}.png`);
      }
      await context.close();
    }
  }
} finally {
  await browser.close();
}

console.log(`[screenshots] Captured ${captured} image(s) into ${outputDirectory}`);
