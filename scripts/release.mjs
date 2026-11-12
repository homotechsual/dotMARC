#!/usr/bin/env node

import {execFileSync} from 'node:child_process';
import {existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync} from 'node:fs';
import {dirname, join} from 'node:path';
import {fileURLToPath} from 'node:url';

const repositoryRoot = join(dirname(fileURLToPath(import.meta.url)), '..');
const projectVersionFile = join(repositoryRoot, 'Directory.Build.props');
const blogDirectory = join(repositoryRoot, 'website', 'blog');
const openApiFile = join(repositoryRoot, 'website', 'data', 'openapi', 'dotmarc-api.json');
const openApiRelativePath = 'website/data/openapi/dotmarc-api.json';

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

function dotnetPackages(args) {
  const output = execFileSync('dotnet', ['list', 'dotMARC.sln', 'package', ...args, '--include-transitive', '--format', 'json'], {
    cwd: repositoryRoot, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, stdio: ['ignore', 'pipe', 'ignore']
  });
  const packages = [];
  for (const project of JSON.parse(output).projects ?? []) {
    for (const framework of project.frameworks ?? []) {
      for (const [kind, key] of [['top-level', 'topLevelPackages'], ['transitive', 'transitivePackages']]) {
        for (const item of framework[key] ?? []) {
          packages.push({kind, project: project.path.split(/[\/]/).pop(), ...item});
        }
      }
    }
  }
  return packages;
}

// Every release ships on the latest packages, new major versions included. A direct package that can't take its latest
// major yet (it needs a newer target framework, say) is listed in scripts/package-holds.json with the reason: it is
// reported on every run but doesn't fail, and it still fails if it falls behind inside the held major. Vulnerable
// packages fail the release. Transitive packages that are behind are listed, because they move when their parent does
// (pin one with a direct PackageReference when it can't wait).
function dependencies(allowOutdated = false) {
  console.log('[release] Checking NuGet packages are on their latest versions...');
  const holds = JSON.parse(readFileSync(join(repositoryRoot, 'scripts', 'package-holds.json'), 'utf8'));
  const major = (version) => Number.parseInt(version.split('.')[0], 10);
  const describe = (item) => `${item.id} ${item.resolvedVersion} (${item.project})`;
  const vulnerable = dotnetPackages(['--vulnerable']);
  const outdated = dotnetPackages(['--outdated']);
  const behindWithinMajor = new Set(dotnetPackages(['--outdated', '--highest-minor']).map((item) => `${item.project}|${item.id}`));

  const problems = vulnerable.map((item) => `${describe(item)} has a known vulnerability (${item.vulnerabilities.map((v) => v.severity).join(', ')})`);
  const held = [];
  for (const item of outdated.filter((candidate) => candidate.kind === 'top-level')) {
    const hold = holds[item.id];
    if (hold?.version === item.resolvedVersion) {
      held.push(`${describe(item)} is held, latest is ${item.latestVersion}: ${hold.reason}`);
    } else if (behindWithinMajor.has(`${item.project}|${item.id}`)) {
      problems.push(`${describe(item)} is behind ${item.latestVersion}`);
    } else if (hold && major(item.resolvedVersion) === hold.major) {
      held.push(`${describe(item)} stays on ${hold.major}.x, latest is ${item.latestVersion}: ${hold.reason}`);
    } else {
      problems.push(`${describe(item)} is behind ${item.latestVersion} (a new major version: upgrade it, or add it to scripts/package-holds.json with the reason)`);
    }
  }

  if (held.length) {
    console.warn(`[release] ${held.length} direct package(s) held back:`);
    for (const line of held) console.warn(`  ${line}`);
  }
  const outdatedTransitive = outdated.filter((item) => item.kind === 'transitive');
  if (outdatedTransitive.length) {
    console.warn(`[release] ${outdatedTransitive.length} transitive package(s) have newer versions:`);
    for (const item of outdatedTransitive) console.warn(`  ${describe(item)} -> ${item.latestVersion}`);
  }

  if (problems.length && !allowOutdated) {
    fail(`Update these before releasing (or pass --allow-outdated):\n  ${problems.join('\n  ')}`);
  }
  console.log(problems.length ? '[release] Continuing with outdated packages (--allow-outdated).' : '[release] Direct packages are up to date.');
}

function check(version, allowExistingTag = false) {
  const slug = slugFor(version);
  const projectVersion = currentProjectVersion();
  if (projectVersion !== version) {
    fail(`Project version is ${projectVersion}, expected ${version}`);
  }

  const openApiVersion = JSON.parse(readFileSync(openApiFile, 'utf8')).info?.version;
  if (openApiVersion !== version) {
    fail(`${openApiRelativePath} is for ${openApiVersion}, expected ${version}. Run: node scripts/update-openapi.mjs`);
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

  // The filename carries the day (Docusaurus and the Canny changelog read it) and the post's own `date` carries the
  // time. Docusaurus sorts posts by date alone, so without a time two releases on the same day list in the wrong order.
  const date = new Date().toISOString().slice(0, 10);
  const blogFile = existingFiles[0] ?? `${date}-${slug}.mdx`;
  const blogPath = join(blogDirectory, blogFile);

  if (currentProjectVersion() !== version) {
    const contents = readFileSync(projectVersionFile, 'utf8');
    writeFileSync(projectVersionFile, contents.replace(/<VersionPrefix>[^<]+<\/VersionPrefix>/, `<VersionPrefix>${version}</VersionPrefix>`));
    console.log(`[release] Updated project version to ${version}`);
    run('node', [join('scripts', 'update-openapi.mjs')]);
    console.log(`[release] Regenerated ${openApiRelativePath} for ${version}`);
  }

  if (!existsSync(blogPath)) {
    mkdirSync(blogDirectory, {recursive: true});
    writeFileSync(blogPath, `---\nslug: ${slug}\ndate: ${new Date().toISOString()}\ntitle: "dotMARC ${slug}: release notes"\nauthors: [mikey]\ntags: [release]\n---\n\n<!-- Replace this with the release notes before running the tag phase. -->\n`);
    console.log(`[release] Created ${blogPath}`);
  } else {
    console.log(`[release] Blog post already exists: ${blogPath}`);
  }

  console.log(`[release] Update any outdated packages (node scripts/release.mjs deps), edit the blog post, then run: node scripts/release.mjs tag ${version}`);
}

function tag(version, allowOutdated = false) {
  check(version);
  dependencies(allowOutdated);
  const slug = slugFor(version);
  const blogFile = blogFilesFor(slug)[0];
  // Not run(): that trims the output, which strips the leading space of the first line
  // (" M Directory.Build.props") and makes the path parsing below drop its first character.
  const status = execFileSync('git', ['status', '--short'], {cwd: repositoryRoot, encoding: 'utf8'});
  const unexpectedChanges = status
    .split('\n')
    .filter(Boolean)
    .map((line) => line.slice(3))
    .filter((file) => file !== 'Directory.Build.props' && file !== `website/blog/${blogFile}` && file !== openApiRelativePath);
  if (unexpectedChanges.length) {
    fail(`Refusing to create a release with unrelated worktree changes: ${unexpectedChanges.join(', ')}`);
  }

  run('git', ['add', 'Directory.Build.props', `website/blog/${blogFile}`, openApiRelativePath]);
  run('git', ['commit', '-m', `Release v${version}`]);
  run('git', ['tag', '-a', `v${version}`, '-m', `Release v${version}`]);
  console.log(`[release] Created commit and tag v${version}`);
}

const [command, rawVersion, option] = process.argv.slice(2);
if (command === 'deps') {
  dependencies(rawVersion === '--allow-outdated');
  process.exit(0);
}
if (!command || !rawVersion || !['prepare', 'check', 'tag', 'deps'].includes(command)) {
  fail('Usage: node scripts/release.mjs <prepare|check|tag> <version> | deps');
}

const version = normalizeVersion(rawVersion);
if (command === 'prepare') prepare(version);
if (command === 'check') check(version, option === '--allow-existing-tag');
if (command === 'tag') tag(version, option === '--allow-outdated');