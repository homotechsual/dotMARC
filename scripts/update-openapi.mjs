#!/usr/bin/env node

// Regenerates website/data/openapi/dotmarc-api.json from the running API, by running the test that compares them with
// DOTMARC_UPDATE_OPENAPI=1. Needs Docker, like the rest of the test suite.
import {execFileSync} from 'node:child_process';
import {dirname, join} from 'node:path';
import {fileURLToPath} from 'node:url';

const repositoryRoot = join(dirname(fileURLToPath(import.meta.url)), '..');
execFileSync('dotnet', ['test', 'test/DotMarc.Tests', '--filter', 'FullyQualifiedName~OpenApiDocumentTests.TheCommittedCopy_MatchesTheApp'], {
  cwd: repositoryRoot,
  stdio: 'inherit',
  env: {...process.env, DOTMARC_UPDATE_OPENAPI: '1'},
});
console.log('[openapi] Updated website/data/openapi/dotmarc-api.json');
