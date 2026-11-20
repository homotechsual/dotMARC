import {execFileSync} from 'node:child_process';

// Which branch a tag may be cut from. A release tag (v1.2.3) publishes the images other people run, so its commit must be on
// main. A pre-release tag (v1.2.3-beta.1) is for work still on develop, so its commit must be on develop. Reachable means the
// commit is on that branch or in its history, so a commit that has been merged into main counts for main, and main's history is
// part of develop's.
const releaseTag = /^v\d+\.\d+\.\d+$/;
const preReleaseTag = /^v\d+\.\d+\.\d+-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*$/;

export function classifyTag(tag) {
  if (releaseTag.test(tag)) {
    return {kind: 'release', branch: 'main'};
  }

  if (preReleaseTag.test(tag)) {
    return {kind: 'pre-release', branch: 'develop'};
  }

  return {kind: 'invalid', branch: null};
}

function git(cwd, args) {
  return execFileSync('git', args, {cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe']}).trim();
}

/** True when the commit is on the remote branch (origin/<branch>) or in its history. Throws when that branch can't be read. */
function isOnBranch(commit, branch, cwd) {
  try {
    git(cwd, ['rev-parse', '--verify', '--quiet', `refs/remotes/origin/${branch}^{commit}`]);
  } catch {
    throw new Error(`Could not check the commit against origin/${branch}, because that branch isn't in this clone. Fetch it first (git fetch origin ${branch}).`);
  }

  try {
    git(cwd, ['merge-base', '--is-ancestor', commit, `refs/remotes/origin/${branch}`]);
    return true;
  } catch (error) {
    if (error.status === 1) {
      return false;
    }

    throw new Error(`Could not check ${commit} against origin/${branch}: ${error.stderr?.toString().trim() || error.message}`);
  }
}

/** Why a release may not be tagged from the branch being worked on, or null when it may. `git tag` itself checks nothing, so
 * release.mjs asks this before it commits and tags. A release is cut on develop, or on main for a fix to the released product. */
export function tagBranchProblem(currentBranch) {
  if (currentBranch === 'develop' || currentBranch === 'main') {
    return null;
  }

  if (currentBranch === 'HEAD') {
    return 'A release can only be tagged from a branch, and this is a detached HEAD. Switch to develop first.';
  }

  return `A release is cut on develop (or on main for a fix to the released product), and this is '${currentBranch}'. Switch to develop first.`;
}

/** Why the tag may not be cut from the commit, or null when it may. A tag that can't be checked is refused, never let through. */
export function tagProblem(tag, commit, cwd) {
  const {kind, branch} = classifyTag(tag);
  if (kind === 'invalid') {
    return `'${tag}' is not a release tag. Use vX.Y.Z for a release, or vX.Y.Z-beta.1 (a hyphen and a label) for a pre-release.`;
  }

  let onBranch;
  try {
    onBranch = isOnBranch(commit, branch, cwd);
  } catch (error) {
    return error.message;
  }

  if (onBranch) {
    return null;
  }

  const short = commit.slice(0, 7);
  return kind === 'release'
    ? `${tag} is a release tag, so its commit must be on main, and ${short} is not. Merge the pull request into main first, then push the tag. Use a merge commit: a squash or rebase merge leaves the tagged commit off main.`
    : `${tag} is a pre-release tag, so its commit must be on develop, and ${short} is not.`;
}
