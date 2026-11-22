const API = 'https://api.github.com';

function base64url(bytes) {
  return btoa(String.fromCharCode(...bytes)).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, '');
}

export async function appJwt(appId, privateKey, now = Date.now()) {
  const encode = value => base64url(new TextEncoder().encode(JSON.stringify(value)));
  const seconds = Math.floor(now / 1000);
  const payload = `${encode({alg: 'RS256', typ: 'JWT'})}.${encode({iat: seconds - 60, exp: seconds + 540, iss: appId})}`;
  const pem = privateKey.replaceAll('\\n', '\n');
  if (!pem.includes('BEGIN PRIVATE KEY')) {
    throw new Error('GitHub App key must be PKCS#8 PEM (BEGIN PRIVATE KEY).');
  }
  const bytes = Uint8Array.from(atob(pem.replace(/-----[^-]+-----|\s/g, '')), char => char.charCodeAt(0));
  const key = await crypto.subtle.importKey('pkcs8', bytes, {name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256'}, false, ['sign']);
  const signature = await crypto.subtle.sign('RSASSA-PKCS1-v1_5', key, new TextEncoder().encode(payload));
  return `${payload}.${base64url(new Uint8Array(signature))}`;
}

export function submissionsConfigured(env) {
  return Boolean(env.GITHUB_APP_ID && env.GITHUB_INSTALLATION_ID && env.GITHUB_PRIVATE_KEY && env.TURNSTILE_SECRET);
}

export function githubClient(env, fetcher = fetch) {
  let token;
  async function authenticate() {
    if (!env.GITHUB_APP_ID || !env.GITHUB_INSTALLATION_ID || !env.GITHUB_PRIVATE_KEY) return null;
    if (!token) {
      const jwt = await appJwt(env.GITHUB_APP_ID, env.GITHUB_PRIVATE_KEY);
      const response = await fetcher(`${API}/app/installations/${encodeURIComponent(env.GITHUB_INSTALLATION_ID)}/access_tokens`, {
        method: 'POST',
        headers: headers(jwt),
        body: JSON.stringify({repositories: [env.GITHUB_REPOSITORY.split('/')[1]], permissions: {issues: 'write', contents: 'read'}}),
        signal: AbortSignal.timeout(15000),
      });
      if (!response.ok) throw new Error(`GitHub App authentication failed (${response.status}).`);
      token = (await response.json()).token;
      if (!token) throw new Error('GitHub App returned no installation token.');
    }
    return token;
  }
  function headers(auth) {
    return {
      Accept: 'application/vnd.github+json',
      'Content-Type': 'application/json',
      'User-Agent': 'dotmarc-feedback-api',
      'X-GitHub-Api-Version': '2022-11-28',
      ...(auth ? {Authorization: `Bearer ${auth}`} : {}),
    };
  }
  async function request(path, method = 'GET', body) {
    const auth = await authenticate();
    if (method !== 'GET' && !auth) throw new Error('GitHub App is not configured for writes.');
    const response = await fetcher(`${API}/repos/${env.GITHUB_REPOSITORY}/${path}`, {
      method, headers: headers(auth),
      ...(body ? {body: JSON.stringify(body)} : {}),
      signal: AbortSignal.timeout(15000),
    });
    if (!response.ok) throw new Error(`GitHub ${method} ${path.split('?')[0]} failed (${response.status}).`);
    return response.json();
  }
  return {
    async list(path) {
      const items = [];
      for (let page = 1; page <= 100; page++) {
        const batch = await request(`${path}${path.includes('?') ? '&' : '?'}per_page=100&page=${page}`);
        if (!Array.isArray(batch)) throw new Error('GitHub list response is not an array.');
        items.push(...batch);
        if (batch.length < 100) return items;
      }
      throw new Error('GitHub pagination limit exceeded; refusing an incomplete roadmap.');
    },
    createIssue: body => request('issues', 'POST', body),
  };
}
