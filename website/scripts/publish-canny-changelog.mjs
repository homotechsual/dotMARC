#!/usr/bin/env node

/**
 * Publishes the release blog post matching VERSION as a Canny changelog entry.
 *
 * The blog post is already the curated changelog content for a release - this converts its MDX
 * to the plain Markdown Canny's API expects and posts it, rather than duplicating that writeup
 * by hand in Canny too.
 *
 * Usage:
 *   VERSION=0.5.0 CANNY_API_KEY=... node website/scripts/publish-canny-changelog.mjs
 */

import {readFileSync, readdirSync} from 'node:fs';
import {join, dirname} from 'node:path';
import {fileURLToPath} from 'node:url';

const SITE_URL = 'https://dotmarc.app';
const BLOG_DIR = join(dirname(fileURLToPath(import.meta.url)), '..', 'blog');

function fail(message) {
  console.error(`[canny-changelog] ${message}`);
  process.exit(1);
}

const version = (process.env.VERSION ?? '').replace(/^v/, '');
if (!/^\d+\.\d+\.\d+$/.test(version)) {
  fail(`VERSION must look like 1.2.3 (got "${process.env.VERSION}")`);
}

const apiKey = process.env.CANNY_API_KEY;
if (!apiKey) {
  fail('CANNY_API_KEY is not set');
}

// Blog posts are named e.g. 2026-09-09-v0-5-0.mdx with a matching `slug: v0-5-0` in frontmatter -
// searching by slug rather than filename avoids relying on the date prefix matching anything.
const slug = `v${version.replace(/\./g, '-')}`;
const fileNames = readdirSync(BLOG_DIR).filter((name) => name.endsWith('.mdx'));
const file = fileNames.find((name) =>
  new RegExp(`^slug:\\s*${slug}\\s*$`, 'm').test(readFileSync(join(BLOG_DIR, name), 'utf8')),
);

if (!file) {
  fail(`No blog post in ${BLOG_DIR} has slug: ${slug}`);
}

// The filename's leading date is the release date - using it as publishedOn means a backfilled
// entry shows up in Canny dated when the release actually shipped, not the day the backfill ran.
const dateMatch = file.match(/^(\d{4}-\d{2}-\d{2})-/);
const publishedOn = dateMatch ? `${dateMatch[1]}T12:00:00.000Z` : undefined;

const notify = (process.env.NOTIFY ?? 'true') !== 'false';

// Normalise line endings: a post authored on Windows can be committed with CRLF, which the
// frontmatter and blank-line patterns below would otherwise not match.
const raw = readFileSync(join(BLOG_DIR, file), 'utf8').replace(/\r\n/g, '\n');

const frontmatterMatch = raw.match(/^---\n([\s\S]*?)\n---\n([\s\S]*)$/);
if (!frontmatterMatch) {
  fail(`${file} has no parseable frontmatter`);
}
const [, frontmatter, body] = frontmatterMatch;

const titleMatch = frontmatter.match(/^title:\s*"?(.*?)"?\s*$/m);
if (!titleMatch) {
  fail(`${file}'s frontmatter has no title`);
}
const title = titleMatch[1];

const markdown = body
  // MDX import statements - Canny has no module system to resolve these against.
  .replace(/^import .+;\s*$/gm, '')
  // The marker Docusaurus uses to cut the blog list preview short; meaningless outside Docusaurus.
  .replace(/\{\/\*\s*truncate\s*\*\/\}\s*/g, '')
  // <ThemedImage alt="..." sources={{ light: useBaseUrl('/x.png'), dark: ... }} /> -> a plain
  // Markdown image using the light variant - Canny has no dark-mode-aware image swapping, and
  // this assumes the `alt` attribute comes before `sources`, matching every use in this repo.
  .replace(
    /<ThemedImage[\s\S]*?alt="([^"]*)"[\s\S]*?light:\s*useBaseUrl\('([^']+)'\)[\s\S]*?\/>/g,
    (_match, alt, src) => `![${alt}](${SITE_URL}${src})`,
  )
  // Relative doc/blog links only resolve on dotmarc.app, not on Canny's own domain.
  .replace(/\]\((\/(?:docs|blog)\/[^)]*)\)/g, `](${SITE_URL}$1)`)
  .replace(/\n{3,}/g, '\n\n')
  .trim();

// Canny's create call has no idempotency key, so re-running a release (or backfilling one that was
// also added by hand) would post a duplicate entry and, with notify on, email subscribers twice.
// Fails closed: if the existing entries can't be listed, nothing is posted.
async function entryExists(entryTitle) {
  for (let skip = 0; ; ) {
    const listResponse = await fetch('https://canny.io/api/v1/entries/list', {
      method: 'POST',
      headers: {'Content-Type': 'application/json'},
      body: JSON.stringify({apiKey, limit: 100, skip}),
    });
    const page = await listResponse.json();
    if (!listResponse.ok || !Array.isArray(page.entries)) {
      fail(`Could not list existing Canny entries (${listResponse.status}): ${JSON.stringify(page).slice(0, 300)}`);
    }
    if (page.entries.some((entry) => entry.title === entryTitle)) {
      return true;
    }
    if (!page.hasMore || page.entries.length === 0) {
      return false;
    }
    skip += page.entries.length;
  }
}

if (await entryExists(title)) {
  console.log(`[canny-changelog] "${title}" already exists in Canny, nothing to publish`);
  process.exit(0);
}

const response = await fetch('https://canny.io/api/v1/entries/create', {
  method: 'POST',
  headers: {'Content-Type': 'application/json'},
  body: JSON.stringify({
    apiKey,
    title,
    details: markdown,
    published: true,
    notify,
    ...(publishedOn ? {publishedOn} : {}),
  }),
});

const result = await response.json();
if (!response.ok || !result.id) {
  fail(`Canny rejected the changelog entry (${response.status}): ${JSON.stringify(result)}`);
}

console.log(
  `[canny-changelog] Published entry ${result.id} for ${slug}: "${title}" ` +
    `(notify=${notify}, publishedOn=${publishedOn ?? 'now'})`,
);
