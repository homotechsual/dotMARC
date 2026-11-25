import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {test} from 'node:test';

const source = await readFile(new URL('../src/DotMarc/wwwroot/listPosition.js', import.meta.url), 'utf8');
const moduleUrl = `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;

function environment() {
  const values = {};
  const store = new Proxy({
    getItem: key => values[key] ?? null,
    setItem: (key, value) => { values[key] = value; },
    removeItem: key => { delete values[key]; },
  }, {ownKeys: () => Object.keys(values), getOwnPropertyDescriptor: () => ({configurable: true, enumerable: true})});
  globalThis.window = {sessionStorage: store, scrollY: 500, addEventListener() {}, removeEventListener() {}, scrollTo() {}};
  globalThis.location = {pathname: '/domains', search: ''};
  globalThis.document = {addEventListener() {}, removeEventListener() {}};
  globalThis.history = {state: {}, replaceState(value) { this.state = value; }, back() { this.wentBack = true; }};
  globalThis.Blazor = {navigateTo(url) { this.url = url; }};
  return values;
}

test('list state restores only its matching entry, route, user and URL', async () => {
  environment();
  const memory = await import(`${moduleUrl}#matching`);
  assert.equal(memory.enter('/domains', 'alice'), null);
  memory.update({search: 'alpha'});
  const marker = history.state.userState;
  memory.leave();
  history.state.userState = marker;
  assert.deepEqual(memory.enter('/domains', 'alice'), {search: 'alpha'});
  memory.leave();
  history.state = {};
  assert.equal(memory.enter('/domains', 'alice'), null); // a menu visit is not a return
  memory.update({search: 'secret'});
  const otherMarker = history.state.userState;
  memory.leave();
  history.state.userState = otherMarker;
  assert.equal(memory.enter('/domains', 'bob'), null);
});

test('entry expires exactly at 30 minutes and ignores a different explicit query', async () => {
  const store = environment();
  const memory = await import(`${moduleUrl}#expiry`);
  memory.enter('/domains', 'alice');
  memory.update({search: 'alpha'});
  const marker = history.state.userState;
  const key = Object.keys(store).find(key => key !== 'dotmarc-list:user');
  const entry = JSON.parse(store[key]);
  memory.leave();
  const now = Date.now;
  try {
    Date.now = () => entry.time + 30 * 60 * 1000;
    store[key] = JSON.stringify(entry);
    history.state.userState = marker;
    assert.equal(memory.enter('/domains', 'alice'), null);
  } finally { Date.now = now; }
  memory.leave();
  store[key] = JSON.stringify({...entry, time: Date.now()});
  history.state.userState = marker;
  location.search = '?target=below';
  assert.equal(memory.enter('/domains', 'alice'), null);
});

test('Back requires a known local origin; otherwise it falls back to Inventory', async () => {
  const store = environment();
  const memory = await import(`${moduleUrl}#back`);
  store['dotmarc-list:user'] = 'alice';
  history.state.userState = JSON.stringify({domainListOrigin: {route: 'https://other.example', version: 1, user: 'alice'}});
  memory.back();
  assert.equal(Blazor.url, '/domains');
  assert.equal(history.wentBack, undefined);
  store['dotmarc-list:token'] = JSON.stringify({version: 1, user: 'alice', route: '/domains', url: '/domains', time: Date.now()});
  history.state.userState = JSON.stringify({domainListOrigin: {route: '/domains', url: '/domains', token: 'token', version: 1, user: 'alice'}});
  memory.back();
  assert.equal(history.wentBack, true);
});

test('corrupt storage and unavailable storage are reported to the caller, not treated as restored', async () => {
  const store = environment();
  const memory = await import(`${moduleUrl}#corrupt`);
  store['dotmarc-list:user'] = 'alice';
  store['dotmarc-list:broken'] = '{bad-json';
  history.state.userState = JSON.stringify({domainListEntry: 'broken'});
  assert.throws(() => memory.enter('/domains', 'alice'), SyntaxError);
  window.sessionStorage = {getItem() { throw new Error('Storage blocked'); }};
  assert.throws(() => memory.enter('/domains', 'alice'), /Storage blocked/);
});
