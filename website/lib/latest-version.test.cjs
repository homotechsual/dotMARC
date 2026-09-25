// Run with: node --test website/lib/latest-version.test.cjs
const assert = require('node:assert/strict');
const {writeFileSync, mkdtempSync} = require('node:fs');
const {tmpdir} = require('node:os');
const {join} = require('node:path');
const {test} = require('node:test');
const {getLatestVersion, releasePostSlug, versionFromProps, versionFromTag} = require('./latest-version.cjs');

const respondWith = (status, body) => async () => ({ok: status >= 200 && status < 300, status, json: async () => body});
const failWith = (message) => async () => {
  throw new Error(message);
};

function propsFileWith(version) {
  const file = join(mkdtempSync(join(tmpdir(), 'dotmarc-props-')), 'Directory.Build.props');
  writeFileSync(file, `<Project><PropertyGroup>\n    <VersionPrefix>${version}</VersionPrefix>\n</PropertyGroup></Project>`);
  return file;
}

test('a release tag becomes a plain version, and anything else is rejected', () => {
  assert.equal(versionFromTag('v0.7.1'), '0.7.1');
  assert.equal(versionFromTag('0.7.1'), '0.7.1');
  assert.equal(versionFromTag(' V1.2.3 '), '1.2.3');
  assert.equal(versionFromTag('v0.7.1-rc.1'), null);
  assert.equal(versionFromTag('latest'), null);
  assert.equal(versionFromTag(undefined), null);
});

test('the version comes out of Directory.Build.props', () => {
  assert.equal(versionFromProps('<Project><PropertyGroup><VersionPrefix>0.7.0</VersionPrefix></PropertyGroup></Project>'), '0.7.0');
  assert.equal(versionFromProps('<Project></Project>'), null);
  assert.equal(versionFromProps(undefined), null);
});

test('a version maps to its release post slug', () => {
  assert.equal(releasePostSlug('0.7.1'), 'v0-7-1');
});

test('the latest GitHub release wins over the version in Directory.Build.props', async () => {
  const version = await getLatestVersion({fetchImplementation: respondWith(200, {tag_name: 'v0.7.0'}), propsPath: propsFileWith('0.7.1')});
  assert.equal(version, '0.7.0');
});

test('the props file is the fallback when GitHub has no release, errors, or is unreachable', async () => {
  const propsPath = propsFileWith('0.7.1');
  assert.equal(await getLatestVersion({fetchImplementation: respondWith(404, {}), propsPath}), '0.7.1');
  assert.equal(await getLatestVersion({fetchImplementation: respondWith(403, {}), propsPath}), '0.7.1');
  assert.equal(await getLatestVersion({fetchImplementation: failWith('offline'), propsPath}), '0.7.1');
});

test('a release tag that is not a plain version is ignored in favour of the fallback', async () => {
  const version = await getLatestVersion({fetchImplementation: respondWith(200, {tag_name: 'nightly'}), propsPath: propsFileWith('0.7.1')});
  assert.equal(version, '0.7.1');
});

test('it returns null when neither source can say', async () => {
  const version = await getLatestVersion({fetchImplementation: failWith('offline'), propsPath: join(tmpdir(), 'no-such-dir', 'Directory.Build.props')});
  assert.equal(version, null);
});
