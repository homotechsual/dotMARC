#!/usr/bin/env node

import {execFileSync} from 'node:child_process';
import {existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync} from 'node:fs';
import {dirname, join} from 'node:path';
import {fileURLToPath} from 'node:url';

const repositoryRoot = join(dirname(fileURLToPath(import.meta.url)), '..');
const projectVersionFile = join(repositoryRoot, 'Directory.Build.props');
const blogDirectory = join(repositoryRoot, 'website', 'blog');

function fail(message) {
  console.error(`[release] ${message}`);
  process.exit(1);
}

function run(command, args) {
  return execFileSync(command, args, {cwd: repositoryRoot, encoding: 'utf8'}).trim();
}

function normalizeVersion(value) {
  const version = value.replace(/^v/, '');
  if (!/^\d+\.\d+\.\d+$/.test(version)) {
    fail(`Version must look like 1.2.3 (got "${value}")`);
  }
  return version;
}

function slugFor(version) {
  return `v${version.replaceAll('.', '-')}`;
}

function blogFilesFor(slug) {
  return readdirSync(blogDirectory)
    .filter((file) => file.endsWith('.mdx'))
    .filter((file) => new RegExp(`^slug:\\s*${slug}\\s*$`, 'm').test(readFileSync(join(blogDirectory, file), 'utf8')));
}

function currentProjectVersion() {
  const contents = readFileSync(projectVersionFile, 'utf8');
  const match = contents.match(/<VersionPrefix>([^<]+)<\/VersionPrefix>/);
  if (!match) {
    fail(`${projectVersionFile} has no VersionPrefix`);
  }
  return match[1];
}

function check(version, allowExistingTag = false) {
  const slug = slugFor(version);
  const projectVersion = currentProjectVersion();
  if (projectVersion !== version) {
    fail(`Project version is ${projectVersion}, expected ${version}`);
  }

  const blogFiles = blogFilesFor(slug);
  if (blogFiles.length !== 1) {
    fail(`Expected exactly one blog post with slug ${slug}, found ${blogFiles.length}`);
  }
  const blogContents = readFileSync(join(blogDirectory, blogFiles[0]), 'utf8');
  if (blogContents.includes('Replace this with the release notes')) {
    fail(`${blogFiles[0]} still contains the generated placeholder`);
  }

  const tag = `v${version}`;
  if (!allowExistingTag && run('git', ['tag', '--list', tag])) {
    fail(`Tag ${tag} already exists`);
  }

  console.log(`[release] ${tag} is ready: ${blogFiles[0]}`);
}

function prepare(version) {
  const slug = slugFor(version);
  const existingFiles = blogFilesFor(slug);
  if (existingFiles.length > 1) {
    fail(`Multiple blog posts already use slug ${slug}`);
  }

  const date = new Date().toISOString().slice(0, 10);
  const blogFile = existingFiles[0] ?? `${date}-${slug}.mdx`;
  const blogPath = join(blogDirectory, blogFile);

  if (currentProjectVersion() !== version) {
    const contents = readFileSync(projectVersionFile, 'utf8');
    writeFileSync(projectVersionFile, contents.replace(/<VersionPrefix>[^<]+<\/VersionPrefix>/, `<VersionPrefix>${version}</VersionPrefix>`));
    console.log(`[release] Updated project version to ${version}`);
  }

  if (!existsSync(blogPath)) {
    mkdirSync(blogDirectory, {recursive: true});
    writeFileSync(blogPath, `---\nslug: ${slug}\ntitle: "dotMARC ${slug}: release notes"\nauthors: [mikey]\ntags: [release]\n---\n\n<!-- Replace this with the release notes before running the tag phase. -->\n`);
    console.log(`[release] Created ${blogPath}`);
  } else {
    console.log(`[release] Blog post already exists: ${blogPath}`);
  }

  console.log(`[release] Edit the blog post, then run: node scripts/release.mjs tag ${version}`);
}

function tag(version) {
  check(version);
  const slug = slugFor(version);
  const blogFile = blogFilesFor(slug)[0];
  const status = run('git', ['status', '--short']);
  const unexpectedChanges = status
    .split('\n')
    .filter(Boolean)
    .map((line) => line.slice(3))
    .filter((file) => file !== 'Directory.Build.props' && file !== `website/blog/${blogFile}`);
  if (unexpectedChanges.length) {
    fail(`Refusing to create a release with unrelated worktree changes: ${unexpectedChanges.join(', ')}`);
  }

  run('git', ['add', 'Directory.Build.props', `website/blog/${blogFile}`]);
  run('git', ['commit', '-m', `Release v${version}`]);
  run('git', ['tag', '-a', `v${version}`, '-m', `Release v${version}`]);
  console.log(`[release] Created commit and tag v${version}`);
}

const [command, rawVersion, option] = process.argv.slice(2);
if (!command || !rawVersion || !['prepare', 'check', 'tag'].includes(command)) {
  fail('Usage: node scripts/release.mjs <prepare|check|tag> <version>');
}

const version = normalizeVersion(rawVersion);
if (command === 'prepare') prepare(version);
if (command === 'check') check(version, option === '--allow-existing-tag');
if (command === 'tag') tag(version);