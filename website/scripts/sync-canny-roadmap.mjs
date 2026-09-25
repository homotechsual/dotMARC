#!/usr/bin/env node

/**
 * Makes sure every idea in canny-roadmap.json exists on the Canny board, tagged with its target
 * version and set to its intended status. Safe to re-run: posts are matched by title, and an
 * existing post keeps its title and status, and only has its version tag brought in line with the
 * roadmap file (the target version added, any other version tag removed), its release line kept in
 * step, and, opt-in via UPDATE_MODE, its description.
 *
 * Canny has no native target version field, so the version is expressed as a tag (for internal
 * filtering and roadmap definitions) and as a "Target release" line at the top of the description
 * (the only place a public visitor will see it).
 *
 * Usage:
 *   CANNY_API_KEY=... DRY_RUN=true node website/scripts/sync-canny-roadmap.mjs
 * Optional: BOARD_ID, AUTHOR_ID (needed only if the board or the admin list is ambiguous).
 *
 * Output goes to public CI logs, so this never prints user emails.
 */

import {readFileSync} from 'node:fs';
import {join, dirname} from 'node:path';
import {fileURLToPath} from 'node:url';
import {
  RELEASE_LINE,
  TARGET_LINE,
  correctReleaseLine,
  releaseLabel,
  shouldMarkComplete,
  statusForNewPost,
} from './canny-release-labels.mjs';

const API_BASE = 'https://canny.io/api/v1';
const DEFAULT_BOARD_TOKEN = '15f43ba5-535f-4bba-bee3-1776018d433b';

const apiKey = process.env.CANNY_API_KEY;
const dryRun = (process.env.DRY_RUN ?? 'true') !== 'false';
// How to treat the description of a post that already exists: leave it alone, add the target
// release line above the current text, or replace it with the description from the roadmap file
// (the original wording is kept underneath as "Original request:").
const updateMode = process.env.UPDATE_MODE ?? 'none';
if (!['none', 'prepend', 'replace'].includes(updateMode)) {
  console.error(`[canny-roadmap] UPDATE_MODE must be none, prepend or replace (got "${updateMode}")`);
  process.exit(1);
}
const notifyVoters = process.env.NOTIFY_VOTERS === 'true';
const VERSION_TAG = /^v\d+\.\d+\.\d+$/i;

// "Released in" and a complete status are only claimed once the version has a GitHub release, so an idea
// that is finished in the code but not yet shipped still reads "Target release". Inside GitHub Actions the
// repository and token come from the environment; elsewhere it falls back to this repository, unauthenticated.
const githubRepository = process.env.GITHUB_REPOSITORY ?? 'homotechsual/dotMARC';
const githubToken = process.env.GITHUB_TOKEN;
const releasedByVersion = new Map();

function log(message) {
  console.log(`[canny-roadmap] ${message}`);
}

function fail(message) {
  console.error(`[canny-roadmap] ${message}`);
  process.exit(1);
}

if (!apiKey) {
  fail('CANNY_API_KEY is not set');
}

/** True when the version has a GitHub release, false when it definitely doesn't, null when that couldn't be
 *  established (a network or rate-limit problem), which callers treat as "don't claim it shipped". */
async function isReleased(version) {
  const key = version.toLowerCase();
  if (releasedByVersion.has(key)) {
    return releasedByVersion.get(key);
  }

  let released = null;
  try {
    const response = await fetch(`https://api.github.com/repos/${githubRepository}/releases/tags/${version}`, {
      headers: {
        Accept: 'application/vnd.github+json',
        ...(githubToken ? {Authorization: `Bearer ${githubToken}`} : {}),
      },
    });
    if (response.status === 200) {
      released = true;
    } else if (response.status === 404) {
      released = false;
    } else {
      log(`Could not tell whether ${version} is released (GitHub answered ${response.status}); treating it as not released`);
    }
  } catch (error) {
    log(`Could not tell whether ${version} is released (${error.message}); treating it as not released`);
  }

  releasedByVersion.set(key, released);
  return released;
}

async function canny(endpoint, body = {}) {
  const response = await fetch(`${API_BASE}/${endpoint}`, {
    method: 'POST',
    headers: {'Content-Type': 'application/json'},
    body: JSON.stringify({apiKey, ...body}),
  });
  const text = await response.text();
  if (!response.ok) {
    throw new Error(`${endpoint} failed (${response.status}): ${text.slice(0, 500)}`);
  }
  if (!text) {
    return {};
  }
  // Some Canny write endpoints (posts/update) reply with the bare text "success", not JSON.
  try {
    return JSON.parse(text);
  } catch {
    return {raw: text};
  }
}

async function listAll(endpoint, key, params = {}) {
  const items = [];
  let skip = 0;
  for (;;) {
    const page = await canny(endpoint, {...params, limit: 100, skip});
    const batch = page[key] ?? [];
    items.push(...batch);
    if (!page.hasMore || batch.length === 0) {
      return items;
    }
    skip += batch.length;
  }
}

async function listAdmins() {
  const admins = [];
  let cursor;
  for (let pageNumber = 0; pageNumber < 50; pageNumber++) {
    const page = await canny('users/list', {limit: 100, ...(cursor ? {cursor} : {})});
    admins.push(...(page.users ?? []).filter((user) => user.isAdmin));
    if (!page.hasNextPage || !page.cursor) {
      break;
    }
    cursor = page.cursor;
  }
  return admins;
}

const normalizeTitle = (title) => title.trim().toLowerCase();

const roadmapPath = join(dirname(fileURLToPath(import.meta.url)), 'canny-roadmap.json');
const roadmap = JSON.parse(readFileSync(roadmapPath, 'utf8')).map((idea) => ({
  ...idea,
  details: Array.isArray(idea.details) ? idea.details.join('\n\n') : idea.details,
}));

log(dryRun ? 'DRY RUN: nothing will be changed' : 'APPLYING changes');

const boards = (await canny('boards/list')).boards ?? [];
log(`Boards: ${boards.map((board) => `${board.name} (${board.id}, ${board.postCount} posts)`).join('; ')}`);
const board = process.env.BOARD_ID
  ? boards.find((candidate) => candidate.id === process.env.BOARD_ID)
  : (boards.find((candidate) => (candidate.token ?? candidate.boardToken) === DEFAULT_BOARD_TOKEN) ??
    (boards.length === 1 ? boards[0] : undefined));
if (!board) {
  fail('Could not pick a board. Set BOARD_ID to one of the ids listed above.');
}
log(`Using board "${board.name}" (${board.id})`);

const existingPosts = await listAll('posts/list', 'posts', {boardID: board.id});
log(`Existing posts on the board (${existingPosts.length}):`);
for (const post of existingPosts) {
  const tagNames = (post.tags ?? []).map((tag) => tag.name).join(', ');
  log(`  - [${post.status}] ${post.title}${tagNames ? ` (tags: ${tagNames})` : ''}`);
}
const existingByTitle = new Map(existingPosts.map((post) => [normalizeTitle(post.title), post]));

let authorId = process.env.AUTHOR_ID;
if (!authorId) {
  // users/list does not necessarily include the account admins, so fall back to admins who have
  // authored or been recorded as the creator of an existing post.
  const adminsById = new Map((await listAdmins()).map((admin) => [admin.id, admin]));
  const postAdmins = existingPosts.flatMap((post) => [post.author, post.by]).filter((user) => user?.isAdmin);
  for (const admin of postAdmins) {
    adminsById.set(admin.id, admin);
  }
  if (adminsById.size !== 1) {
    const candidates = [...adminsById.values()].map((admin) => `${admin.id} (${admin.name})`).join(', ') || 'none found';
    const sampleAuthorKeys = Object.keys(existingPosts[0]?.author ?? {}).join(', ') || 'no posts to inspect';
    fail(
      `Expected exactly one Canny admin to author posts, found ${adminsById.size}: ${candidates}. ` +
        `Post author fields seen: ${sampleAuthorKeys}. Set AUTHOR_ID.`,
    );
  }
  authorId = [...adminsById.keys()][0];
  log('Authoring posts as the only Canny admin found');
}

const boardTags = (await listAll('tags/list', 'tags', {boardID: board.id})).filter(
  (tag) => !tag.boardID || tag.boardID === board.id,
);
const tagIdByName = new Map(boardTags.map((tag) => [tag.name.toLowerCase(), tag.id]));

const neededVersions = [...new Set(roadmap.map((idea) => idea.version).filter(Boolean))];
for (const version of neededVersions) {
  if (tagIdByName.has(version.toLowerCase())) {
    continue;
  }
  if (dryRun) {
    log(`Would create tag ${version}`);
    continue;
  }
  const created = await canny('tags/create', {boardID: board.id, name: version});
  if (!created.id) {
    fail(`tags/create returned no id for ${version}: ${JSON.stringify(created)}`);
  }
  tagIdByName.set(version.toLowerCase(), created.id);
  log(`Created tag ${version}`);
}

let failures = 0;
for (const idea of roadmap) {
  const existing = existingByTitle.get(normalizeTitle(idea.title));
  const versionTagId = idea.version ? tagIdByName.get(idea.version.toLowerCase()) : undefined;

  const released = idea.version && idea.status === 'complete' ? await isReleased(idea.version) : undefined;
  const releaseText = releaseLabel(idea, released);
  const fullDetails = idea.version ? `${releaseText}: ${idea.version}\n\n${idea.details}` : idea.details;

  try {
    if (existing) {
      const needsTag =
        Boolean(idea.version) &&
        !(existing.tags ?? []).some((tag) => tag.name.toLowerCase() === idea.version.toLowerCase());
      // Moving an idea to a different release: drop its old version tag(s) so it isn't listed under
      // two releases. Only tags shaped like a version are touched; any other tag (for example
      // "In Dev") belongs to the board owner and is left alone.
      const staleVersionTags = idea.version
        ? (existing.tags ?? []).filter(
            (tag) => VERSION_TAG.test(tag.name) && tag.name.toLowerCase() !== idea.version.toLowerCase(),
          )
        : [];
      // Statuses are otherwise the board owner's to manage; the only move made here is to mark
      // something complete once the roadmap file says it has shipped and its version has been released.
      const needsCompletion = shouldMarkComplete(idea, existing.status, released);

      const currentDetails = existing.details ?? '';
      let updatedDetails;
      let correctedDetails;
      let detailsChange = '';
      if (updateMode !== 'none' && idea.version && !RELEASE_LINE.test(currentDetails)) {
        const original = currentDetails.trim();
        // Replacing keeps the original wording underneath, unless it only repeats the title.
        const keepOriginal = original && normalizeTitle(original) !== normalizeTitle(existing.title);
        if (updateMode === 'prepend' && original) {
          updatedDetails = `${releaseText}: ${idea.version}\n\n${currentDetails}`;
        } else {
          updatedDetails = keepOriginal ? `${fullDetails}\n\nOriginal request: ${original}` : fullDetails;
        }
        detailsChange = `${updateMode} description`;
      } else if ((correctedDetails = correctReleaseLine(currentDetails, idea, released)) !== null) {
        // Once the version is released a complete idea's "Target release" line becomes "Released in", whatever
        // the update mode; and a "Released in" written before the release goes back to "Target release".
        // Only the first line is touched.
        updatedDetails = correctedDetails;
        detailsChange = released
          ? 'change release line to "Released in"'
          : 'change release line back to "Target release" (the version is not released yet)';
      } else if (idea.version && idea.status !== 'complete') {
        // The idea moved to another release, so its public "Target release" line must follow.
        const currentTarget = currentDetails.match(/^Target release:\s*(\S+)/);
        if (currentTarget && currentTarget[1].toLowerCase() !== idea.version.toLowerCase()) {
          updatedDetails = currentDetails.replace(TARGET_LINE, `Target release: ${idea.version}`);
          detailsChange = `change target release to ${idea.version}`;
        }
      }

      const pendingChanges = [
        needsTag ? 'add version tag' : '',
        staleVersionTags.length > 0 ? `remove ${staleVersionTags.map((tag) => tag.name).join(', ')} tag` : '',
        updatedDetails ? detailsChange : '',
        needsCompletion ? `mark complete${notifyVoters ? ' and notify voters' : ''}` : '',
      ]
        .filter(Boolean)
        .join(' + ');

      if (dryRun) {
        log(`${pendingChanges ? `Would ${pendingChanges} on` : 'EXISTS  '} "${existing.title}"`);
        log(`    current description (${currentDetails.length} chars): ${JSON.stringify(currentDetails.slice(0, 240))}`);
        continue;
      }
      if (needsTag && versionTagId) {
        await canny('posts/add_tag', {postID: existing.id, tagID: versionTagId});
      }
      for (const staleTag of staleVersionTags) {
        await canny('posts/remove_tag', {postID: existing.id, tagID: staleTag.id});
      }
      if (updatedDetails) {
        await canny('posts/update', {postID: existing.id, details: updatedDetails});
      }
      if (needsCompletion) {
        await canny('posts/change_status', {
          changerID: authorId,
          postID: existing.id,
          shouldNotifyVoters: notifyVoters,
          status: 'complete',
        });
      }
      log(`${pendingChanges ? `UPDATED  (${pendingChanges})` : 'EXISTS  '} ${existing.title}`);
      continue;
    }

    if (dryRun) {
      log(`Would create "${idea.title}" (${idea.version ?? 'no version'}, ${statusForNewPost(idea, released)})`);
      continue;
    }

    const created = await canny('posts/create', {
      authorID: authorId,
      boardID: board.id,
      title: idea.title,
      details: fullDetails,
    });
    if (versionTagId) {
      await canny('posts/add_tag', {postID: created.id, tagID: versionTagId});
    }
    await canny('posts/change_status', {
      changerID: authorId,
      postID: created.id,
      shouldNotifyVoters: false,
      status: statusForNewPost(idea, released),
    });
    log(`CREATED  ${idea.title} (${idea.version ?? 'no version'}, ${statusForNewPost(idea, released)}) -> ${created.id}`);
  } catch (error) {
    failures++;
    console.error(`[canny-roadmap] FAILED   ${idea.title}: ${error.message}`);
  }
}

if (failures > 0) {
  fail(`${failures} idea(s) failed, see above`);
}
log(dryRun ? 'Dry run complete' : 'Sync complete');
