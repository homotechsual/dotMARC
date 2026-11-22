import {githubClient, submissionsConfigured} from './github.mjs';
import {buildRoadmap} from './roadmap.mjs';

const MINUTE = 60000;
const encoder = new TextEncoder();

function json(data, status = 200, headers = {}) {
  return Response.json(data, {status, headers: {'Cache-Control': 'no-store', ...headers}});
}

class InputError extends Error {
  constructor(message, status = 400) { super(message); this.status = status; }
}

export async function readBody(request, limit) {
  const reader = request.body?.getReader();
  if (!reader) throw new InputError('A request body is required.');
  let size = 0;
  const chunks = [];
  for (;;) {
    const {done, value} = await reader.read();
    if (done) break;
    size += value.length;
    if (size > limit) { await reader.cancel(); throw new InputError('Request is too large.', 413); }
    chunks.push(value);
  }
  const bytes = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
  return bytes;
}

export function validateSubmission(value) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new InputError('Invalid submission.');
  const {title, description, useCase, turnstileToken, requestId, publicConsent} = value;
  if (typeof title !== 'string' || title.trim().length < 8 || title.trim().length > 160 || /[\r\n]/.test(title)) {
    throw new InputError('Use a title between 8 and 160 characters on one line.');
  }
  if (typeof description !== 'string' || description.trim().length < 20 || description.length > 4000) {
    throw new InputError('Describe the request in 20 to 4,000 characters.');
  }
  if (typeof useCase !== 'string' || useCase.trim().length < 10 || useCase.length > 2000) {
    throw new InputError('Describe the use case in 10 to 2,000 characters.');
  }
  if (publicConsent !== true) throw new InputError('Confirm that the request will be public.');
  if (typeof turnstileToken !== 'string' || !turnstileToken || turnstileToken.length > 2048) throw new InputError('Complete the anti-spam check.');
  if (typeof requestId !== 'string' || !/^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i.test(requestId)) throw new InputError('Invalid request identifier.');
  return {title: title.trim(), description: description.trim(), useCase: useCase.trim(), turnstileToken, requestId};
}

export async function verifySignature(body, signature, secret) {
  if (!secret || !/^sha256=[0-9a-f]{64}$/.test(signature ?? '')) return false;
  const key = await crypto.subtle.importKey('raw', encoder.encode(secret), {name: 'HMAC', hash: 'SHA-256'}, false, ['verify']);
  const digest = Uint8Array.from(signature.slice(7).match(/../g), hex => parseInt(hex, 16));
  return crypto.subtle.verify('HMAC', key, digest, body);
}

function roadmapStub(env) { return env.ROADMAP.get(env.ROADMAP.idFromName('public-roadmap')); }

export class RoadmapCache {
  constructor(state, env) { this.state = state; this.env = env; this.refreshing = null; }
  async refresh() {
    if (this.refreshing) return this.refreshing;
    this.refreshing = (async () => {
      const github = githubClient(this.env);
      const [issues, milestones, releases] = await Promise.all([
        github.list('issues?state=all&labels=roadmap%3Apublic'),
        github.list('milestones?state=all'), github.list('releases'),
      ]);
      const data = buildRoadmap(issues, milestones, releases);
      await this.state.storage.put('snapshot', data);
      await this.state.storage.delete('failure');
      return data;
    })();
    try { return await this.refreshing; }
    catch (error) {
      console.error('Roadmap refresh failed:', error.message);
      await this.state.storage.put('failure', {at: Date.now()});
      throw error;
    } finally { this.refreshing = null; }
  }
  async fetch(request) {
    let snapshot = await this.state.storage.get('snapshot');
    const failure = await this.state.storage.get('failure');
    const force = request.method === 'POST';
    const age = snapshot ? Date.now() - Date.parse(snapshot.updatedAt) : Infinity;
    if (force || (age > 10 * MINUTE && (!failure || Date.now() - failure.at > MINUTE))) {
      try { snapshot = await this.refresh(); return json({...snapshot, stale: false}); }
      catch { return snapshot ? json({...snapshot, stale: true}) : json({error: 'Roadmap is temporarily unavailable.'}, 503); }
    }
    if (!snapshot) return json({error: 'Roadmap is temporarily unavailable.'}, 503);
    return json({...snapshot, stale: age > 10 * MINUTE && !force && Boolean(failure)});
  }
}

export class SubmissionGate {
  constructor(state) { this.state = state; }
  async fetch(request) {
    const input = await request.json();
    const now = Date.now();
    return this.state.storage.transaction(async storage => {
      const records = (await storage.get('requests') ?? []).filter(record => now - record.at < 60 * MINUTE);
      const previous = records.find(record => record.id === input.id);
      if (new URL(request.url).pathname === '/claim') {
        if (previous) return json(previous.result ? {issue: previous.result} : {error: 'This submission may already have been sent. Check GitHub before submitting again.'}, previous.result ? 200 : 409);
        if (records.length >= 3) return json({error: 'Too many requests. Please try again in an hour.'}, 429, {'Retry-After': '3600'});
        records.push({id: input.id, at: now});
        await storage.put('requests', records);
        await storage.setAlarm(now + 60 * MINUTE);
        return json({claimed: true});
      }
      if (!previous) return json({error: 'Submission reservation expired.'}, 409);
      previous.result = input.issue;
      await storage.put('requests', records);
      return json({saved: true});
    });
  }
  async alarm() { await this.state.storage.deleteAll(); }
}

async function submit(request, env) {
  if (!submissionsConfigured(env)) return json({error: 'Website submissions are not configured yet. Please submit directly on GitHub.'}, 503);
  if (!request.headers.get('Content-Type')?.startsWith('application/json')) throw new InputError('Send JSON.', 415);
  let parsed;
  try { parsed = JSON.parse(new TextDecoder('utf-8', {fatal: true}).decode(await readBody(request, 10000))); }
  catch (error) { if (error instanceof InputError) throw error; throw new InputError('Invalid JSON.'); }
  const value = validateSubmission(parsed);
  const ip = request.headers.get('CF-Connecting-IP');
  if (!ip) throw new InputError('Could not verify the submission origin.', 403);
  const verification = await fetch('https://challenges.cloudflare.com/turnstile/v0/siteverify', {
    method: 'POST', headers: {'Content-Type': 'application/json'},
    body: JSON.stringify({secret: env.TURNSTILE_SECRET, response: value.turnstileToken, remoteip: ip}),
    signal: AbortSignal.timeout(15000),
  });
  if (!verification.ok) throw new Error(`Turnstile verification failed (${verification.status}).`);
  const challenge = await verification.json();
  if (!challenge.success || challenge.hostname !== new URL(env.SITE_ORIGIN).hostname || challenge.action !== 'feature-request') {
    throw new InputError('Anti-spam verification failed. Please try again.', 403);
  }
  const hash = new Uint8Array(await crypto.subtle.digest('SHA-256', encoder.encode(ip)));
  const identity = Array.from(hash, byte => byte.toString(16).padStart(2, '0')).join('');
  const gate = env.SUBMISSIONS.get(env.SUBMISSIONS.idFromName(identity));
  const claim = await gate.fetch('https://internal/claim', {method: 'POST', body: JSON.stringify({id: value.requestId})});
  if (!claim.ok) return claim;
  const reservation = await claim.json();
  if (reservation.issue) return json({issue: reservation.issue});
  const issue = await githubClient(env).createIssue({
    title: value.title,
    body: `## Feature request\n\n${value.description}\n\n## Use case\n\n${value.useCase}\n\n---\nSubmitted anonymously through dotmarc.app. No contact information was collected.\n<!-- dotmarc-submission:${value.requestId} -->`,
    labels: ['type:feature', 'status:triage'],
  });
  const result = {number: issue.number, url: issue.html_url};
  const saved = await gate.fetch('https://internal/complete', {method: 'POST', body: JSON.stringify({id: value.requestId, issue: result})});
  if (!saved.ok) console.error('Issue created but submission receipt could not be saved:', issue.number);
  return json({issue: result}, 201);
}

export default {
  async fetch(request, env) {
    const origin = request.headers.get('Origin');
    const path = new URL(request.url).pathname;
    const cors = origin === env.SITE_ORIGIN ? {
      'Access-Control-Allow-Origin': origin, Vary: 'Origin',
      'Access-Control-Allow-Methods': 'GET, POST, OPTIONS', 'Access-Control-Allow-Headers': 'Content-Type',
    } : {};
    try {
      let response;
      if (path === '/webhook' && request.method === 'POST') {
        const body = await readBody(request, 256000);
        if (!await verifySignature(body, request.headers.get('X-Hub-Signature-256'), env.GITHUB_WEBHOOK_SECRET)) return json({error: 'Invalid webhook signature.'}, 401);
        let event;
        try { event = JSON.parse(new TextDecoder().decode(body)); }
        catch { throw new InputError('Invalid webhook JSON.'); }
        const kind = request.headers.get('X-GitHub-Event');
        if (kind === 'ping') return json({ok: true});
        if (event.repository?.full_name !== env.GITHUB_REPOSITORY) return json({error: 'Unexpected repository.'}, 403);
        if (!['issues', 'milestone', 'release'].includes(kind)) return json({ignored: true});
        response = await roadmapStub(env).fetch('https://internal/refresh', {method: 'POST'});
        if (!response.ok || (await response.clone().json()).stale) return json({error: 'Roadmap refresh failed; scheduled reconciliation will retry.'}, 503);
        return json({ok: true});
      }
      if (request.method === 'OPTIONS') {
        if (!Object.keys(cors).length) return json({error: 'Origin not allowed.'}, 403);
        return new Response(null, {status: 204, headers: cors});
      }
      if (path === '/roadmap' && request.method === 'GET') {
        response = await roadmapStub(env).fetch('https://internal/read');
        const data = await response.json();
        response = json({...data, submissionsEnabled: submissionsConfigured(env)}, response.status);
      } else if (path === '/requests' && request.method === 'POST') {
        if (origin !== env.SITE_ORIGIN) return json({error: 'Origin not allowed.'}, 403);
        response = await submit(request, env);
      } else response = json({error: 'Not found.'}, 404);
      response = new Response(response.body, {status: response.status, headers: response.headers});
      for (const [key, value] of Object.entries(cors)) response.headers.set(key, value);
      return response;
    } catch (error) {
      if (!(error instanceof InputError)) console.error('Feedback API failed:', error.message);
      return json({error: error instanceof InputError ? error.message : 'The service could not complete the request. For submissions, check GitHub before retrying: the issue may already exist.'},
        error instanceof InputError ? error.status : 503, cors);
    }
  },
  async scheduled(_event, env, ctx) {
    ctx.waitUntil((async () => {
      const response = await roadmapStub(env).fetch('https://internal/refresh', {method: 'POST'});
      if (!response.ok || (await response.json()).stale) throw new Error('Scheduled roadmap refresh failed.');
    })());
  },
};
