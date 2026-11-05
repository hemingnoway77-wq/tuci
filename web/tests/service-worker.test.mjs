import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { createHash } from 'node:crypto';

const source = await readFile(new URL('../sw.template.js', import.meta.url), 'utf8');
function worker({ precache = async () => {}, keys = [] } = {}) {
  const events = {}, calls = [];
  const self = {
    registration: { scope: 'https://example.test/tuci/' },
    location: { origin: 'https://example.test' },
    addEventListener: (name, listener) => { events[name] = listener; },
    skipWaiting: async () => { calls.push('skipWaiting'); },
    clients: { claim: async () => { calls.push('claim'); } },
  };
  const caches = {
    open: async () => ({ addAll: precache }),
    keys: async () => keys,
    delete: async name => { calls.push(['delete', name]); },
  };
  vm.runInNewContext(source.replace('"__VERSION__"', '"new"').replace('"__ASSETS__"', JSON.stringify({ 'index.html': 'sha256-test' })), { self, caches, URL, Request });
  const run = name => {
    let completion;
    events[name]({ waitUntil: promise => { completion = promise; } });
    return completion;
  };
  return { run, calls, events };
}

test('a verified upgrade activates while old tabs are still open, only after precaching completes', async () => {
  let complete;
  const cached = new Promise(resolve => { complete = resolve; });
  const sw = worker({ precache: () => cached });
  const installation = sw.run('install');
  await Promise.resolve();
  assert.deepEqual(sw.calls, []);
  complete();
  await installation;
  assert.deepEqual(sw.calls, ['skipWaiting']);
});

test('a failed or incomplete download leaves the existing worker in control', async () => {
  const sw = worker({ precache: async () => { throw new Error('integrity mismatch'); } });
  await assert.rejects(sw.run('install'), /integrity mismatch/);
  assert.deepEqual(sw.calls, []);
});

test('activation removes only this app scope caches and claims clients without navigating their forms', async () => {
  const sw = worker({ keys: ['tuci:web:/tuci/:old', 'tuci:web:/tuci/:new', 'tuci:web:/other/:old', 'unrelated'] });
  await sw.run('activate');
  assert.deepEqual(sw.calls, [['delete', 'tuci:web:/tuci/:old'], 'claim']);
});

test('the standalone recovery script is permitted by its content security policy', async () => {
  const page = await readFile(new URL('../update.html', import.meta.url), 'utf8');
  const script = page.match(/<script>([\s\S]*?)<\/script>/)[1];
  const digest = createHash('sha256').update(script).digest('base64');
  assert.ok(page.includes(`script-src 'sha256-${digest}'`));
  assert.doesNotMatch(script, /localStorage\.(clear|removeItem)|indexedDB\.deleteDatabase|caches\.delete|\.unregister\(/);
});

test('recovery retains a candidate that fails quickly instead of accepting the old active worker', async () => {
  const page = await readFile(new URL('../update.html', import.meta.url), 'utf8');
  const script = page.match(/<script>([\s\S]*?)<\/script>/)[1];
  const candidate = { state: 'installing', addEventListener() {}, removeEventListener() {} };
  const registration = {
    installing: candidate, waiting: null, active: { ...candidate, state: 'activated' },
    addEventListener() {}, removeEventListener() {},
    async update() { candidate.state = 'redundant'; this.installing = null; },
  };
  const status = {}, retry = { addEventListener() {} }, redirects = [];
  await vm.runInNewContext(script, {
    document: { querySelector: selector => selector === '#status' ? status : retry },
    navigator: { serviceWorker: { register: async () => registration } },
    location: { replace: path => redirects.push(path) }, setTimeout, clearTimeout,
  });
  assert.deepEqual(redirects, []);
  assert.equal(retry.hidden, false);
});
