import {execFileSync} from 'node:child_process';
import {appendFileSync} from 'node:fs';
import {pathToFileURL} from 'node:url';

// Keep these in sync with CI's push.paths; the test suite checks both lists.
export const applicationPaths = [
  'src/**',
  'test/**',
  'Directory.Build.props',
  'Directory.Build.targets',
  'Directory.Packages.props',
  'global.json',
  'nuget.config',
  'dotMARC.sln',
  '.dockerignore',
  'docker-compose.demo.yml',
  'scripts/deployment-url.sh',
  'scripts/verify-deployment.sh',
  'scripts/ci-scope.mjs',
  'scripts/ci-scope.test.mjs',
  '.github/workflows/ci.yml',
  '.github/workflows/publish.yml',
  '.github/workflows/demo-deploy.yml',
];

export function isApplicationPath(path) {
  return applicationPaths.some(pattern => pattern.endsWith('/**')
    ? path.startsWith(pattern.slice(0, -2))
    : path === pattern);
}

export function needsApplicationTests({eventName, baseSha, headSha, cwd}) {
  if (eventName !== 'pull_request') return true;
  if (!baseSha || !headSha) throw new Error('Pull request CI requires both base and head commit SHAs.');

  // Disable rename detection so moving an application file out of src/ still counts as a deletion.
  const paths = execFileSync('git', [
    'diff', '--name-only', '--no-renames', '-z', `${baseSha}...${headSha}`, '--',
  ], {cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], maxBuffer: 32 * 1024 * 1024}).split('\0').filter(Boolean);
  return paths.some(isApplicationPath);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const needed = needsApplicationTests({
    eventName: process.env.GITHUB_EVENT_NAME,
    baseSha: process.env.CI_BASE_SHA,
    headSha: process.env.CI_HEAD_SHA,
  });
  if (!process.env.GITHUB_OUTPUT) throw new Error('GITHUB_OUTPUT is required.');
  appendFileSync(process.env.GITHUB_OUTPUT, `needed=${needed}\n`);
  console.log(needed
    ? 'Application/build inputs changed, or this is not a PR: running the .NET suite.'
    : 'No application/build inputs changed: skipping the .NET suite; the required test check still reports.');
}
