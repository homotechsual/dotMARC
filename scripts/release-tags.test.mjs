import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {mkdtempSync, rmSync, writeFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {dirname, join} from 'node:path';
import {after, before, describe, test} from 'node:test';
import {fileURLToPath} from 'node:url';
import {classifyTag, tagBranchProblem, tagProblem} from './release-tags.mjs';

const releaseScript = join(dirname(fileURLToPath(import.meta.url)), 'release.mjs');

function git(cwd, ...args) {
  return execFileSync('git', ['-c', 'user.name=Test', '-c', 'user.email=test@example.com', ...args], {cwd, encoding: 'utf8'}).trim();
}

describe('classifyTag', () => {
  test('a version is a release tag, which belongs on main', () => {
    assert.deepEqual(classifyTag('v1.2.3'), {kind: 'release', branch: 'main'});
    assert.deepEqual(classifyTag('v0.13.0'), {kind: 'release', branch: 'main'});
  });

  test('a version with a label is a pre-release tag, which belongs on develop', () => {
    assert.deepEqual(classifyTag('v1.2.3-beta.1'), {kind: 'pre-release', branch: 'develop'});
    assert.deepEqual(classifyTag('v1.2.3-rc.2'), {kind: 'pre-release', branch: 'develop'});
    assert.deepEqual(classifyTag('v1.2.3-alpha'), {kind: 'pre-release', branch: 'develop'});
  });

  test('anything else is not a tag the pipeline understands', () => {
    for (const tag of ['1.2.3', 'v1.2', 'v1.2.3.4', 'v1.2.3-', 'v1.2.3-beta..1', 'v1.2.3+build', 'latest', 'next', '']) {
      assert.deepEqual(classifyTag(tag), {kind: 'invalid', branch: null}, `"${tag}"`);
    }
  });
});

describe('tagProblem', () => {
  let repo;
  let onMain;
  let onDevelopOnly;
  let onNeither;

  before(() => {
    repo = mkdtempSync(join(tmpdir(), 'dotmarc-tags-'));
    git(repo, 'init', '-q', '-b', 'main');
    const commit = (name) => {
      writeFileSync(join(repo, name), name);
      git(repo, 'add', name);
      git(repo, 'commit', '-q', '-m', name);
      return git(repo, 'rev-parse', 'HEAD');
    };

    onMain = commit('released.txt');
    git(repo, 'update-ref', 'refs/remotes/origin/main', onMain);
    git(repo, 'checkout', '-q', '-b', 'develop');
    onDevelopOnly = commit('unreleased.txt');
    git(repo, 'update-ref', 'refs/remotes/origin/develop', onDevelopOnly);
    git(repo, 'checkout', '-q', '-b', 'feature/elsewhere', onMain);
    onNeither = commit('elsewhere.txt');
  });

  after(() => rmSync(repo, {recursive: true, force: true}));

  test('a release tag on a commit that is on main is fine', () => {
    assert.equal(tagProblem('v1.2.3', onMain, repo), null);
  });

  test('a release tag on a commit that is only on develop is refused, and says to merge into main first', () => {
    const problem = tagProblem('v1.2.3', onDevelopOnly, repo);

    assert.match(problem, /v1\.2\.3 is a release tag/);
    assert.match(problem, /must be on main/);
    assert.match(problem, /Merge .*into main first, then push the tag/);
  });

  test('a release tag on a commit that is on neither branch is refused', () => {
    assert.match(tagProblem('v1.2.3', onNeither, repo), /must be on main/);
  });

  test('a pre-release tag on a commit that is on develop is fine, including one that has since reached main', () => {
    assert.equal(tagProblem('v1.2.3-beta.1', onDevelopOnly, repo), null);
    assert.equal(tagProblem('v1.2.3-beta.1', onMain, repo), null, 'main\'s history is part of develop\'s');
  });

  test('a pre-release tag on a commit that is not on develop is refused', () => {
    const problem = tagProblem('v1.2.3-beta.1', onNeither, repo);

    assert.match(problem, /v1\.2\.3-beta\.1 is a pre-release tag/);
    assert.match(problem, /must be on develop/);
  });

  test('a tag the pipeline does not understand is refused, with the shapes that are allowed', () => {
    const problem = tagProblem('banana', onMain, repo);

    assert.match(problem, /'banana' is not a release tag/);
    assert.match(problem, /vX\.Y\.Z/);
  });

  test('a branch that is missing cannot be checked, so the tag is refused rather than let through', () => {
    const bare = mkdtempSync(join(tmpdir(), 'dotmarc-tags-bare-'));
    try {
      git(bare, 'init', '-q', '-b', 'main');
      writeFileSync(join(bare, 'a.txt'), 'a');
      git(bare, 'add', 'a.txt');
      git(bare, 'commit', '-q', '-m', 'a');
      const sha = git(bare, 'rev-parse', 'HEAD');

      assert.match(tagProblem('v1.2.3', sha, bare), /Could not check .* origin\/main/);
    } finally {
      rmSync(bare, {recursive: true, force: true});
    }
  });
});

describe('tagBranchProblem', () => {
  test('a release is cut on develop, or on main for a fix to the released product', () => {
    assert.equal(tagBranchProblem('develop'), null);
    assert.equal(tagBranchProblem('main'), null);
  });

  test('any other branch is refused, and says where to cut it', () => {
    assert.match(tagBranchProblem('feature/alerts'), /develop/);
    assert.match(tagBranchProblem('fix/tickets'), /feature\/|fix\/|'fix\/tickets'/);
  });

  test('a detached head is refused', () => {
    assert.match(tagBranchProblem('HEAD'), /detached/);
  });
});

describe('release.mjs verify-tag', () => {
  test('refuses a tag that is not a release or pre-release tag, with a non-zero exit', () => {
    assert.throws(
      () => execFileSync('node', [releaseScript, 'verify-tag', 'banana', 'HEAD'], {encoding: 'utf8', stdio: 'pipe'}),
      (error) => error.status === 1 && /'banana' is not a release tag/.test(error.stderr),
    );
  });
});
