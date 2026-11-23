import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {mkdtempSync, mkdirSync, readFileSync, renameSync, rmSync, writeFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {dirname, join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {test} from 'node:test';
import {applicationPaths, isApplicationPath, needsApplicationTests} from './ci-scope.mjs';

const scriptDirectory = dirname(fileURLToPath(import.meta.url));

test('every configured application input triggers the suite', () => {
  for (const pattern of applicationPaths) {
    assert.equal(isApplicationPath(pattern.replace('**', 'nested/input.cs')), true, pattern);
  }
});

test('non-application changes do not trigger .NET, but mixed changes do', () => {
  const paths = [
    'README.md', 'docs/superpowers/plans/release.md', 'website/src/pages/index.tsx',
    '.github/ISSUE_TEMPLATE/feature-request.yml', '.github/workflows/feedback-worker.yml',
    '.github/workflows/website-ci.yml', 'scripts/release-tags.mjs',
    'src-not-application/file.cs',
  ];
  assert.equal(paths.some(isApplicationPath), false);
  assert.equal([...paths, 'src/DotMarc/Program.cs'].some(isApplicationPath), true);
});

test('push path filter and classifier cover exactly the same inputs', () => {
  const workflow = readFileSync(join(scriptDirectory, '..', '.github', 'workflows', 'ci.yml'), 'utf8').replaceAll('\r\n', '\n');
  const pushSection = workflow.split('  push:\n')[1].split('  pull_request:')[0];
  const paths = [...pushSection.matchAll(/^      - "([^"]+)"$/gm)].map(match => match[1]);
  assert.deepEqual(paths.sort(), [...applicationPaths].sort());
  assert.match(workflow, /^  pull_request:\s*$/m);
});

test('non-PR events always run without a diff', () => {
  for (const eventName of ['push', 'workflow_dispatch', 'workflow_call']) {
    assert.equal(needsApplicationTests({eventName}), true);
  }
});

test('PR diffs handle docs, mixed changes, deletion, rename and invalid refs', t => {
  const cwd = mkdtempSync(join(tmpdir(), 'dotmarc-ci-scope-'));
  t.after(() => rmSync(cwd, {recursive: true, force: true}));
  const git = (...args) => execFileSync('git', [
    '-c', 'user.name=Test', '-c', 'user.email=test@example.com', ...args,
  ], {cwd, encoding: 'utf8'}).trim();
  const commit = () => {
    git('add', '-A');
    git('commit', '-qm', 'fixture');
    return git('rev-parse', 'HEAD');
  };
  const check = (baseSha, headSha) => needsApplicationTests({eventName: 'pull_request', baseSha, headSha, cwd});
  git('init', '-q');
  mkdirSync(join(cwd, 'src'));
  mkdirSync(join(cwd, 'docs'));
  writeFileSync(join(cwd, 'src', 'input.cs'), 'application');
  const base = commit();
  assert.equal(check(base, base), false);
  writeFileSync(join(cwd, 'docs', 'space name.md'), 'documentation');
  assert.equal(check(base, commit()), false);
  writeFileSync(join(cwd, 'src', 'input.cs'), 'changed application');
  const mixed = commit();
  assert.equal(check(base, mixed), true);
  renameSync(join(cwd, 'src', 'input.cs'), join(cwd, 'docs', 'moved.cs'));
  const renamed = commit();
  assert.equal(check(mixed, renamed), true);
  writeFileSync(join(cwd, 'src', 'deleted.cs'), 'delete me');
  const beforeDeletion = commit();
  rmSync(join(cwd, 'src', 'deleted.cs'));
  assert.equal(check(beforeDeletion, commit()), true);
  assert.throws(() => check(base, 'missing-ref'), /Command failed/);
  assert.throws(() => check(undefined, base), /requires both/);

  const output = join(cwd, 'output.txt');
  const result = execFileSync(process.execPath, [join(scriptDirectory, 'ci-scope.mjs')], {
    cwd,
    env: {...process.env, GITHUB_EVENT_NAME: 'pull_request', CI_BASE_SHA: renamed, CI_HEAD_SHA: renamed, GITHUB_OUTPUT: output},
    encoding: 'utf8',
  });
  assert.match(result, /skipping the .NET suite/);
  assert.equal(readFileSync(output, 'utf8'), 'needed=false\n');
  assert.throws(() => execFileSync(process.execPath, [join(scriptDirectory, 'ci-scope.mjs')], {
    cwd,
    env: {...process.env, GITHUB_EVENT_NAME: 'pull_request', CI_BASE_SHA: base, CI_HEAD_SHA: 'missing-ref', GITHUB_OUTPUT: output},
    stdio: 'pipe',
  }));
  assert.equal(readFileSync(output, 'utf8'), 'needed=false\n', 'failed diff must not emit a success-shaped output');
});
