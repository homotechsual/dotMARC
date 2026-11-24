import {test} from 'node:test';
import assert from 'node:assert/strict';
import {generateKeyPairSync, verify} from 'node:crypto';
import worker, {validateSubmission, readBody, verifySignature, RoadmapCache, SubmissionGate} from '../src/index.mjs';
import {buildRoadmap} from '../src/roadmap.mjs';
import {appJwt, githubClient} from '../src/github.mjs';

const issue = (number, extra = {}) => ({
  number, title: `Feature ${number}`, html_url: `https://github.com/homotechsual/dotMARC/issues/${number}`,
  labels: [{name: 'roadmap:public'}, {name: 'status:planned'}], state: 'open', state_reason: null, ...extra,
});
const milestone = (number, title, extra = {}) => ({
  number, title, state: 'open', description: 'Planned scope', html_url: 'https://github.com/homotechsual/dotMARC/milestone/1', due_on: null, ...extra,
});
const submission = {
  changeType: 'enhancement', areas: ['dns', 'spf-dkim'],
  title: 'A useful feature', description: 'Please add this helpful capability.', useCase: 'It makes our work easier.',
  publicConsent: true, turnstileToken: 'test-token', requestId: '89c2ee34-6c12-42ba-9e3a-4aa5b163a102',
};
const repository = 'homotechsual/dotMARC';

class Storage {
  values = new Map();
  async get(key) { return structuredClone(this.values.get(key)); }
  async put(key, value) { this.values.set(key, structuredClone(value)); }
  async delete(key) { this.values.delete(key); }
  async deleteAll() { this.values.clear(); }
  async setAlarm(time) { this.alarm = time; }
  async transaction(callback) { return callback(this); }
}
const post = (path, body) => new Request(`https://internal/${path}`, {method: 'POST', body: JSON.stringify(body)});

test('roadmap omits non-public issues, pull requests, duplicates and declined requests', () => {
  const result = buildRoadmap([
    issue(1), issue(2, {labels: []}), issue(3, {pull_request: {}}),
    issue(4, {state: 'closed', state_reason: 'not_planned'}),
    issue(5, {labels: [{name: 'roadmap:public'}, {name: 'duplicate'}]}),
  ], [], []);
  assert.deepEqual(result.issues.map(value => value.number), [1]);
});

test('open release:ready work counts as completed and awaiting release, but stays in progress', () => {
  const plan = milestone(1, 'v0.14.0');
  const ready = issue(1, {milestone: plan, labels: [{name: 'roadmap:public'}, {name: 'status:in-progress'}, {name: 'release:ready'}]});
  const underway = issue(2, {milestone: plan, labels: [{name: 'roadmap:public'}, {name: 'status:in-progress'}]});
  const results = buildRoadmap([ready, underway], [plan], []).issues;
  const readyResult = results.find(result => result.number === 1);
  const underwayResult = results.find(result => result.number === 2);
  assert.deepEqual([readyResult.status, readyResult.completed, readyResult.awaitingRelease, readyResult.statusWarning], ['in-progress', true, true, false]);
  assert.deepEqual([underwayResult.status, underwayResult.completed, underwayResult.awaitingRelease], ['in-progress', false, false]);
});

test('completed work is not shipped until a stable GitHub release exists', () => {
  const plan = milestone(1, 'v0.14.0 — Action centre');
  const done = issue(1, {state: 'closed', state_reason: 'completed', milestone: plan});
  for (const releases of [[], [{tag_name: 'v0.14.0', prerelease: true}], [{tag_name: 'v0.14.0', draft: true}]]) {
    assert.equal(buildRoadmap([done], [plan], releases).issues[0].awaitingRelease, true);
  }
  const result = buildRoadmap([done, issue(2, {milestone: plan})], [plan], [
    {tag_name: 'v0.14.0', published_at: '2026-12-01T00:00:00Z', html_url: 'https://github.com/homotechsual/dotMARC/releases/tag/v0.14.0'},
  ]);
  assert.equal(result.issues[0].status, 'planned');
  assert.equal(result.issues[1].status, 'shipped');
  assert.equal(result.milestones[0].released, true);
});

test('closed milestone is not proof of release; plans use numeric version ordering', () => {
  const result = buildRoadmap([], [
    milestone(1, 'v0.9.0'), milestone(2, 'v0.14.0'), milestone(3, 'v0.15.0', {state: 'closed'}),
    milestone(4, 'Internal maintenance'),
  ], []);
  assert.deepEqual(result.milestones.map(value => value.number), [1, 2]);
});

test('multiple or missing status labels produce a visible status warning', () => {
  const result = buildRoadmap([issue(1, {labels: [{name: 'roadmap:public'}]}),
    issue(2, {labels: [{name: 'roadmap:public'}, {name: 'status:planned'}, {name: 'status:considering'}]})], [], []);
  assert.ok(result.issues.every(value => value.statusWarning && value.status === 'triage'));
});

test('submission validates boundaries and consent', () => {
  assert.equal(validateSubmission(submission).title, submission.title);
  for (const patch of [{title: 'short'}, {title: 'a\nlong title'}, {description: 'short'},
    {useCase: ''}, {publicConsent: false}, {requestId: 'invalid'}, {turnstileToken: ''},
    {title: 'a'.repeat(161)}, {description: 'a'.repeat(4001)}, {useCase: 'a'.repeat(2001)},
    {changeType: 'unknown'}, {changeType: undefined}, {areas: []}, {areas: ['dns', 'dns']},
    {areas: ['arbitrary-label']}, {areas: 'dns'}]) {
    assert.throws(() => validateSubmission({...submission, ...patch}));
  }
  assert.doesNotThrow(() => validateSubmission({...submission, title: 'a'.repeat(160), description: 'a'.repeat(4000), useCase: 'a'.repeat(2000)}));
});

test('optional GitHub username requires explicit mention consent and excludes mention injection', () => {
  assert.equal(validateSubmission(submission).githubUsername, '');
  assert.equal(validateSubmission({...submission, githubUsername: ' @example-user ', mentionConsent: true}).githubUsername, 'example-user');
  for (const githubUsername of ['a--b', '-user', 'user-', 'user/team', 'user@example.com', 'user\n@other', 'a'.repeat(40)]) {
    assert.throws(() => validateSubmission({...submission, githubUsername, mentionConsent: true}));
  }
  assert.throws(() => validateSubmission({...submission, githubUsername: 'example-user'}), /consent/);
});

test('roadmap exposes multiple areas and distinct change types, with explicit ambiguity', () => {
  const result = buildRoadmap([
    issue(1, {labels: ['roadmap:public', 'type:feature', 'area:dns', 'area:spf-dkim'].map(name => ({name}))}),
    issue(2, {labels: ['roadmap:public', 'type:bug', 'area:domains'].map(name => ({name}))}),
    issue(3, {labels: ['roadmap:public', 'enhancement', 'area:other'].map(name => ({name}))}),
    issue(4, {labels: ['roadmap:public', 'type:feature', 'type:bug'].map(name => ({name}))}),
  ], [], []).issues;
  assert.equal(result.find(issue => issue.number === 1).changeType, 'feature');
  assert.deepEqual(result.find(issue => issue.number === 1).areas, ['dns', 'spf-dkim']);
  assert.equal(result.find(issue => issue.number === 2).changeType, 'bug');
  assert.equal(result.find(issue => issue.number === 3).changeType, 'enhancement');
  assert.equal(result.find(issue => issue.number === 4).changeType, null);
  assert.equal(result.find(issue => issue.number === 4).classificationWarning, true);
});

test('body limit counts streamed bytes, not only Content-Length', async () => {
  await assert.rejects(() => readBody(post('test', {value: 'x'.repeat(30)}), 20), /too large/);
  assert.ok((await readBody(post('test', {ok: true}), 20)).length > 0);
});

test('webhook verifies HMAC signature and rejects tampering', async () => {
  const bytes = new TextEncoder().encode('{"event":"issues"}');
  const key = await crypto.subtle.importKey('raw', new TextEncoder().encode('test-secret'), {name: 'HMAC', hash: 'SHA-256'}, false, ['sign']);
  const signature = new Uint8Array(await crypto.subtle.sign('HMAC', key, bytes));
  const hex = Array.from(signature, byte => byte.toString(16).padStart(2, '0')).join('');
  assert.equal(await verifySignature(bytes, `sha256=${hex}`, 'test-secret'), true);
  assert.equal(await verifySignature(new TextEncoder().encode('tampered'), `sha256=${hex}`, 'test-secret'), false);
  assert.equal(await verifySignature(bytes, 'sha256=invalid', 'test-secret'), false);
});

test('GitHub App JWT uses PKCS8 key, correct lifetime and valid signature', async () => {
  const {privateKey, publicKey} = generateKeyPairSync('rsa', {modulusLength: 2048});
  const now = 1800000000000;
  const jwt = await appJwt('1234', privateKey.export({format: 'pem', type: 'pkcs8'}), now);
  const [header, payload, signature] = jwt.split('.');
  assert.deepEqual(JSON.parse(Buffer.from(header, 'base64url')), {alg: 'RS256', typ: 'JWT'});
  assert.deepEqual(JSON.parse(Buffer.from(payload, 'base64url')), {iss: '1234', iat: now / 1000 - 60, exp: now / 1000 + 540});
  assert.equal(verify('RSA-SHA256', Buffer.from(`${header}.${payload}`), publicKey, Buffer.from(signature, 'base64url')), true);
  await assert.rejects(() => appJwt('1234', '-----BEGIN RSA PRIVATE KEY-----'), /PKCS/);
});

test('GitHub list paginates and errors rather than using incomplete data', async () => {
  const urls = [];
  const client = githubClient({GITHUB_REPOSITORY: repository}, async url => {
    urls.push(url);
    return Response.json(new URL(url).searchParams.get('page') === '1' ? Array.from({length: 100}, () => ({})) : [{}]);
  });
  assert.equal((await client.list('issues?state=all')).length, 101);
  assert.ok(urls[1].includes('&per_page=100&page=2'));
  await assert.rejects(() => githubClient({GITHUB_REPOSITORY: repository}, async () => new Response('', {status: 403})).list('issues'), /403/);
});

test('submission gate reserves once, returns receipt and limits three per hour', async () => {
  const storage = new Storage();
  const gate = new SubmissionGate({storage});
  assert.equal((await gate.fetch(post('claim', {id: 'one'}))).status, 200);
  assert.equal((await gate.fetch(post('claim', {id: 'one'}))).status, 409);
  await gate.fetch(post('complete', {id: 'one', issue: {number: 1, url: 'https://github.com/example'}}));
  assert.equal((await (await gate.fetch(post('claim', {id: 'one'}))).json()).issue.number, 1);
  await gate.fetch(post('claim', {id: 'two'}));
  await gate.fetch(post('claim', {id: 'three'}));
  const limited = await gate.fetch(post('claim', {id: 'four'}));
  assert.equal(limited.status, 429);
  assert.equal(limited.headers.get('Retry-After'), '3600');
  assert.ok(storage.alarm > Date.now());
  await gate.alarm();
  assert.equal(storage.values.size, 0);
});

test('rate-limit window expires exactly one hour after reservation', async () => {
  const originalNow = Date.now;
  let now = 1800000000000;
  Date.now = () => now;
  try {
    const gate = new SubmissionGate({storage: new Storage()});
    for (const id of ['one', 'two', 'three']) assert.equal((await gate.fetch(post('claim', {id}))).status, 200);
    now += 3600000 - 1;
    assert.equal((await gate.fetch(post('claim', {id: 'four'}))).status, 429);
    now += 1;
    assert.equal((await gate.fetch(post('claim', {id: 'four'}))).status, 200);
  } finally { Date.now = originalNow; }
});

test('authenticated webhook restricts repository and reports refresh failure', async () => {
  const secret = 'webhook-test-only';
  const env = {
    GITHUB_REPOSITORY: repository, GITHUB_WEBHOOK_SECRET: secret,
    ROADMAP: {idFromName: name => name, get: () => ({fetch: async () => Response.json({stale: true})})},
  };
  async function request(repo) {
    const body = JSON.stringify({repository: {full_name: repo}});
    const key = await crypto.subtle.importKey('raw', new TextEncoder().encode(secret), {name: 'HMAC', hash: 'SHA-256'}, false, ['sign']);
    const digest = new Uint8Array(await crypto.subtle.sign('HMAC', key, new TextEncoder().encode(body)));
    const signature = Array.from(digest, byte => byte.toString(16).padStart(2, '0')).join('');
    return new Request('https://api/webhook', {
      method: 'POST', body, headers: {'X-Hub-Signature-256': `sha256=${signature}`, 'X-GitHub-Event': 'issues'},
    });
  }
  assert.equal((await worker.fetch(await request('other/repository'), env)).status, 403);
  assert.equal((await worker.fetch(await request(repository), env)).status, 503);
});

test('roadmap retains explicitly stale snapshot after refresh failure and recovers', async () => {
  const storage = new Storage();
  const cache = new RoadmapCache({storage}, {});
  const old = {updatedAt: new Date(Date.now() - 20 * 60000).toISOString(), issues: [], milestones: []};
  await storage.put('snapshot', old);
  cache.refresh = async () => { throw new Error('GitHub unavailable'); };
  const failed = await cache.fetch(new Request('https://internal/read'));
  assert.equal((await failed.json()).stale, true);
  const fresh = {...old, updatedAt: new Date().toISOString()};
  cache.refresh = async () => fresh;
  assert.equal((await (await cache.fetch(new Request('https://internal/read'))).json()).stale, false);
});

test('API exposes CORS only for allowed origin and rejects unconfigured writes', async () => {
  const env = {SITE_ORIGIN: 'https://dotmarc.app', GITHUB_REPOSITORY: repository};
  const forbidden = await worker.fetch(new Request('https://api/requests', {method: 'POST', headers: {Origin: 'https://evil.example'}}), env);
  assert.equal(forbidden.status, 403);
  const disabled = await worker.fetch(new Request('https://api/requests', {method: 'POST', headers: {Origin: env.SITE_ORIGIN}}), env);
  assert.equal(disabled.status, 503);
  assert.equal(disabled.headers.get('Access-Control-Allow-Origin'), env.SITE_ORIGIN);
  const preflight = await worker.fetch(new Request('https://api/requests', {method: 'OPTIONS', headers: {Origin: env.SITE_ORIGIN}}), env);
  assert.equal(preflight.status, 204);
  const badSignature = await worker.fetch(post('webhook', {repository: {full_name: repository}}), env);
  assert.equal(badSignature.status, 401);
});

test('full submission verifies Turnstile and creates correctly labelled issue once', async () => {
  const {privateKey} = generateKeyPairSync('rsa', {modulusLength: 2048});
  const gate = new SubmissionGate({storage: new Storage()});
  const env = {
    SITE_ORIGIN: 'https://dotmarc.app', GITHUB_REPOSITORY: repository,
    GITHUB_APP_ID: '1', GITHUB_INSTALLATION_ID: '2',
    GITHUB_PRIVATE_KEY: privateKey.export({format: 'pem', type: 'pkcs8'}), TURNSTILE_SECRET: 'test-only',
    SUBMISSIONS: {idFromName: name => name, get: () => ({fetch: (url, options) => gate.fetch(new Request(url, options))})},
  };
  const oldFetch = globalThis.fetch;
  let writes = 0;
  globalThis.fetch = async (url, options) => {
    if (url.includes('siteverify')) return Response.json({success: true, hostname: 'dotmarc.app', action: 'feature-request'});
    if (url.includes('access_tokens')) return Response.json({token: 'installation-test-token'});
    if (url.endsWith('/issues')) {
      writes++;
      const body = JSON.parse(options.body);
      assert.deepEqual(body.labels, ['type:enhancement', 'area:dns', 'area:spf-dkim', 'status:triage']);
      assert.match(body.body, /### Change type\n\nEnhancement/);
      assert.match(body.body, /### Product areas\n\nDNS & provider push, SPF & DKIM/);
      assert.match(body.body, /username not verified\): @example-user/);
      assert.ok(body.body.includes(`dotmarc-submission:${submission.requestId}`));
      return Response.json({number: 42, html_url: `https://github.com/${repository}/issues/42`});
    }
    throw new Error(`Unexpected URL ${url}`);
  };
  try {
    const request = () => new Request('https://api/requests', {
      method: 'POST', headers: {Origin: env.SITE_ORIGIN, 'Content-Type': 'application/json', 'CF-Connecting-IP': '192.0.2.1'},
      body: JSON.stringify({...submission, githubUsername: 'example-user', mentionConsent: true}),
    });
    assert.equal((await worker.fetch(request(), env)).status, 201);
    assert.equal((await (await worker.fetch(request(), env)).json()).issue.number, 42);
    assert.equal(writes, 1);
  } finally { globalThis.fetch = oldFetch; }
});
