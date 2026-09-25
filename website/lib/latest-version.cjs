'use strict';

/**
 * Works out the latest released dotMARC version at build time, so the docs and the homepage can say it without
 * anyone editing them at every release.
 *
 * The source of truth is the latest published GitHub release: a version is only "latest" once its release exists
 * (and so its image), which is later than the commit that bumps Directory.Build.props. If GitHub can't be reached
 * (an offline build, a rate limit), the version in Directory.Build.props is the fallback; on main that is the last
 * released version, apart from the short window while a release is being prepared.
 */

const {readFileSync} = require('node:fs');
const {join} = require('node:path');

const SEMVER = /^\d+\.\d+\.\d+$/;

/** "v0.7.0" or "0.7.0" becomes "0.7.0"; anything that isn't a plain release version becomes null. */
function versionFromTag(tag) {
  if (typeof tag !== 'string') {
    return null;
  }
  const version = tag.trim().replace(/^v/i, '');
  return SEMVER.test(version) ? version : null;
}

/** The <VersionPrefix> in a Directory.Build.props file's text, or null. */
function versionFromProps(propsText) {
  const match = /<VersionPrefix>\s*([^<\s]+)\s*<\/VersionPrefix>/.exec(propsText ?? '');
  return match ? versionFromTag(match[1]) : null;
}

/** The slug of a version's release post: 0.7.0 becomes v0-7-0, matching the post's front matter. */
function releasePostSlug(version) {
  return `v${version.replaceAll('.', '-')}`;
}

async function versionFromGitHub({repository, token, fetchImplementation = fetch}) {
  try {
    const response = await fetchImplementation(`https://api.github.com/repos/${repository}/releases/latest`, {
      headers: {
        Accept: 'application/vnd.github+json',
        ...(token ? {Authorization: `Bearer ${token}`} : {}),
      },
      signal: AbortSignal.timeout(8000),
    });
    if (!response.ok) {
      return null;
    }
    return versionFromTag((await response.json()).tag_name);
  } catch {
    return null;
  }
}

/**
 * @returns the latest released version such as "0.7.0", or null when neither source could say (callers then show
 *   "latest", which is also a valid image tag).
 */
async function getLatestVersion({
  repository = process.env.GITHUB_REPOSITORY ?? 'homotechsual/dotMARC',
  token = process.env.GITHUB_TOKEN,
  propsPath = join(__dirname, '..', '..', 'Directory.Build.props'),
  fetchImplementation = fetch,
} = {}) {
  const fromGitHub = await versionFromGitHub({repository, token, fetchImplementation});
  if (fromGitHub) {
    return fromGitHub;
  }

  try {
    return versionFromProps(readFileSync(propsPath, 'utf8'));
  } catch {
    return null;
  }
}

module.exports = {getLatestVersion, versionFromTag, versionFromProps, releasePostSlug};
