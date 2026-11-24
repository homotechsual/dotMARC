import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {test} from 'node:test';

const read = path => readFileSync(new URL(path, import.meta.url), 'utf8').replaceAll('\r\n', '\n');
const ci = read('../.github/workflows/website-ci.yml');
const publish = read('../.github/workflows/website-publish.yml');

test('publication depends on CI and deploys its artifact without rebuilding', () => {
  assert.match(publish, /uses: \.\/\.github\/workflows\/website-ci\.yml/);
  assert.match(publish, /source-ref: \$\{\{ github.event.pull_request.head.sha \|\| github.sha \}\}/);
  const deploy = publish.split('  deploy:\n')[1];
  assert.match(deploy, /needs: validate/);
  assert.match(deploy, /actions\/download-artifact@v4/);
  assert.match(deploy, /name: website-build/);
  assert.match(deploy, /path: \$\{\{ runner.temp \}\}\/website-deploy\/build/);
  assert.match(deploy, /workingDirectory: \$\{\{ runner.temp \}\}\/website-deploy/);
  assert.match(deploy, /packageManager: npm/);
  assert.doesNotMatch(deploy, /workingDirectory: website/);
  assert.doesNotMatch(deploy, /yarn build|yarn install|always\(\)/);
  assert.match(ci, /ref: \$\{\{ inputs.source-ref \}\}/);
  assert.ok(ci.indexOf('Upload tested website') > ci.indexOf('Test feedback and roadmap in browser'));
  assert.match(ci, /actions\/upload-artifact@v4/);
  assert.match(ci, /name: website-build/);
  assert.match(ci, /include-hidden-files: true/);
  assert.match(ci, /if-no-files-found: error/);
});

test('only publication owns event triggers; forks validate without publishing', () => {
  assert.match(ci, /  workflow_call:/);
  assert.doesNotMatch(ci, /^  (push|pull_request|workflow_dispatch):/m);
  assert.match(publish, /^  push:/m);
  assert.match(publish, /^  pull_request:/m);
  assert.match(publish, /^  workflow_dispatch:/m);
  assert.match(publish, /publish-build: \$\{\{ github.event_name != 'pull_request' \|\| github.event.pull_request.head.repo.fork == false \}\}/);
  assert.match(publish.split('  deploy:\n')[1], /if: \$\{\{ github.event_name != 'pull_request' \|\| github.event.pull_request.head.repo.fork == false \}\}/);
  assert.doesNotMatch(ci, /secrets: inherit|CLOUDFLARE_API_TOKEN/);
  assert.match(ci, /if: inputs.publish-build/);
  assert.match(ci, /FEEDBACK_TURNSTILE_SITE_KEY is required for a publishable build/);
});
