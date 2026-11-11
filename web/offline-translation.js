const paths = new Map([
  ['translation-worker.js', 'text/javascript'],
  ['bergamot-translator.js', 'text/javascript'],
  ['bergamot-translator.wasm', 'application/wasm'],
  ['en-zh/model.enzh.intgemm.alphas.bin', 'application/octet-stream'],
  ['en-zh/lex.50.50.enzh.s2t.bin', 'application/octet-stream'],
  ['en-zh/srcvocab.enzh.spm', 'application/octet-stream'],
  ['en-zh/trgvocab.enzh.spm', 'application/octet-stream']
]);
const canceled = () => new DOMException('请求已取消。', 'AbortError');
const invalid = () => new Error('离线翻译文件校验失败，请重新下载。');
const unavailable = () => new Error('离线翻译暂不可用，请稍后重试。');
const check = signal => { if (signal?.aborted) throw canceled(); };
const digest = async bytes => Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)),
  value => value.toString(16).padStart(2, '0')).join('');

function readManifest(value) {
  if (value?.formatVersion !== 1 || !/^[a-f0-9]{16}$/.test(value.version) ||
      !value.files || Object.keys(value.files).length !== paths.size) throw invalid();
  const files = [];
  for (const [path, type] of paths) {
    const asset = value.files[path];
    if (asset?.type !== type || !Number.isSafeInteger(asset.bytes) || asset.bytes < 1 ||
        asset.bytes > 50_000_000 || !/^[a-f0-9]{64}$/.test(asset.sha256)) throw invalid();
    files.push({ path, type, bytes: asset.bytes, sha256: asset.sha256 });
  }
  const totalBytes = files.reduce((sum, asset) => sum + asset.bytes, 0);
  if (totalBytes > 60_000_000) throw invalid();
  return { version: value.version, files, totalBytes };
}

export function createOfflineTranslation({ baseURL = new URL('./', import.meta.url),
  fetcher = (...args) => fetch(...args), cacheStorage = globalThis.caches,
  createWorker = url => new Worker(url, { name: 'TUCI English to Chinese' }),
  workerReady = () => !!globalThis.navigator?.serviceWorker?.controller && typeof Worker === 'function' } = {}) {
  const root = new URL(baseURL), base = new URL('offline-translation/', root);
  const manifestURL = new URL('manifest.json', base).href;
  const cachePrefix = 'tuci:translation:' + root.pathname + ':';
  const urlFor = (info, path) => new URL(info.version + '/' + path, base).href;
  let manifestValue, worker, workerVersion, idleTimer, nextId = 0;
  const requests = new Map();

  async function manifest({ signal, cachedOnly = false } = {}) {
    check(signal);
    if (manifestValue) return manifestValue;
    let response;
    try { response = await cacheStorage?.match?.(manifestURL); } catch { /* Storage can be disabled. */ }
    check(signal);
    if (!response && cachedOnly) return null;
    if (!response) response = await fetcher(manifestURL, { signal, credentials: 'omit', mode: 'same-origin', redirect: 'error' });
    if (!response?.ok || response.redirected || response.type === 'opaque' ||
        Number(response.headers.get('Content-Length')) > 8192) throw unavailable();
    const raw = await response.text();
    check(signal);
    if (raw.length > 8192) throw invalid();
    manifestValue = readManifest(JSON.parse(raw));
    return manifestValue;
  }

  async function cacheFor(info, required = false) {
    try { return await cacheStorage?.open(cachePrefix + info.version); }
    catch { if (required) throw new Error('浏览器未允许保存离线翻译，请检查网站存储设置。'); }
  }

  function progress(info, completed, bytes) {
    return { complete: completed === info.files.length, completed, total: info.files.length, bytes, totalBytes: info.totalBytes };
  }

  async function hasAsset(cache, info, asset) {
    const stored = await cache?.match(urlFor(info, asset.path));
    return !!stored && stored.headers.get('Content-Length') === String(asset.bytes) && stored.headers.get('X-Tuci-SHA256') === asset.sha256;
  }

  async function statusFor(info, cache, signal) {
    let completed = 0, bytes = 0;
    for (const asset of info.files) {
      check(signal);
      if (await hasAsset(cache, info, asset)) { completed++; bytes += asset.bytes; }
    }
    check(signal);
    return progress(info, completed, bytes);
  }

  async function status({ signal } = {}) {
    const info = await manifest({ signal });
    return statusFor(info, await cacheFor(info), signal);
  }

  async function downloadAsset(info, asset, signal, report) {
    const address = new URL(urlFor(info, asset.path));
    // This request is explicitly initiated by the download button. Worker
    // lookups use the same URL without the query and are served cache-only.
    address.searchParams.set('download', info.version);
    const controller = new AbortController();
    const abort = () => controller.abort();
    signal?.addEventListener('abort', abort, { once: true });
    const deadline = setTimeout(abort, 180_000);
    try {
      check(signal);
      const response = await fetcher(address.href, { signal: controller.signal, credentials: 'omit',
        mode: 'same-origin', redirect: 'error', cache: 'no-store', referrerPolicy: 'no-referrer' });
      if (!response.ok || response.redirected || response.type === 'opaque' ||
          (response.url && new URL(response.url).origin !== base.origin)) throw unavailable();
      if (Number(response.headers.get('Content-Length')) > asset.bytes) throw invalid();
      const bytes = new Uint8Array(asset.bytes), reader = response.body?.getReader();
      if (!reader) throw unavailable();
      let size = 0;
      const cancelRead = () => { void reader.cancel().catch(() => {}); };
      controller.signal.addEventListener('abort', cancelRead, { once: true });
      try {
        if (controller.signal.aborted) cancelRead();
        for (;;) {
          const { done, value } = await reader.read();
          check(signal);
          if (controller.signal.aborted) throw unavailable();
          if (done) break;
          if (size + value.byteLength > asset.bytes) { cancelRead(); throw invalid(); }
          bytes.set(value, size); size += value.byteLength; report(size);
        }
      } finally { controller.signal.removeEventListener('abort', cancelRead); reader.releaseLock(); }
      if (size !== asset.bytes || await digest(bytes) !== asset.sha256) throw invalid();
      check(signal);
      return new Response(bytes, { headers: { 'Content-Type': asset.type, 'Content-Length': String(asset.bytes), 'X-Tuci-SHA256': asset.sha256 } });
    } catch (error) {
      check(signal);
      if (controller.signal.aborted) throw new Error('离线翻译下载超时，已验证的文件会保留。');
      throw error;
    } finally { clearTimeout(deadline); signal?.removeEventListener('abort', abort); }
  }

  async function download({ signal, onProgress = () => {} } = {}) {
    const info = await manifest({ signal }), cache = await cacheFor(info, true);
    if (!cache) throw new Error('此浏览器无法保存离线翻译文件。');
    let { completed, bytes } = await statusFor(info, cache, signal);
    onProgress(progress(info, completed, bytes));
    for (const asset of info.files) {
      check(signal);
      if (await hasAsset(cache, info, asset)) continue;
      const response = await downloadAsset(info, asset, signal, size => onProgress(progress(info, completed, bytes + size)));
      check(signal);
      try { await cache.put(urlFor(info, asset.path), response); }
      catch { throw new Error('离线翻译文件未能保存，请检查可用存储空间后继续下载。'); }
      completed++; bytes += asset.bytes;
      onProgress(progress(info, completed, bytes));
    }
    check(signal);
    return progress(info, completed, bytes);
  }

  function finish(id, error, text) {
    const pending = requests.get(id);
    if (!pending) return;
    requests.delete(id);
    clearTimeout(pending.timeout);
    pending.signal?.removeEventListener('abort', pending.abort);
    if (error) pending.reject(error); else pending.resolve(text);
    if (!requests.size && worker) {
      idleTimer = setTimeout(() => stop(), 45_000);
      idleTimer?.unref?.();
    }
  }

  function stop(error = unavailable()) {
    clearTimeout(idleTimer);
    worker?.terminate(); worker = null; workerVersion = null;
    for (const id of requests.keys()) finish(id, error);
  }

  function engine(info) {
    if (worker && workerVersion === info.version) return worker;
    stop();
    const current = createWorker(urlFor(info, 'translation-worker.js'));
    worker = current; workerVersion = info.version;
    current.onmessage = event => {
      if (worker !== current) return;
      const message = event.data;
      if (message?.type === 'result' && typeof message.text === 'string' && message.text.trim())
        finish(message.id, null, message.text.trim());
      else if (message?.type === 'error') {
        if (Number.isSafeInteger(message.id)) finish(message.id, unavailable());
        else stop();
      }
    };
    current.onerror = event => { event.preventDefault?.(); if (worker === current) stop(); };
    return current;
  }

  async function translate(text, { signal } = {}) {
    check(signal);
    if (typeof text !== 'string' || !text.trim() || text.length > 2000 || !workerReady()) return null;
    const info = await manifest({ signal, cachedOnly: true });
    if (!info || !(await statusFor(info, await cacheFor(info), signal)).complete) return null;
    if (!worker || workerVersion !== info.version) {
      // A previous app worker may still control a page during an update. A
      // cache-only probe confirms the current route before creating the Worker.
      try {
        const asset = info.files[0];
        const cached = await fetcher(urlFor(info, asset.path), { signal, cache: 'only-if-cached',
          mode: 'same-origin', credentials: 'omit', redirect: 'error' });
        const ready = cached.ok && cached.headers.get('X-Tuci-SHA256') === asset.sha256;
        await cached.body?.cancel().catch(() => {});
        if (!ready) return null;
      } catch { check(signal); return null; }
    }
    check(signal);
    const current = engine(info), id = ++nextId;
    clearTimeout(idleTimer);
    return new Promise((resolve, reject) => {
      const abort = () => { try { current.postMessage({ type: 'cancel', id }); } finally { finish(id, canceled()); } };
      const timeout = setTimeout(() => stop(), 50_000);
      requests.set(id, { resolve, reject, signal, abort, timeout });
      signal?.addEventListener('abort', abort, { once: true });
      try { current.postMessage({ type: 'translate', id, text }); }
      catch { finish(id, unavailable()); }
    });
  }

  return { status, download, translate };
}

let shared;
function translation() { return shared ||= createOfflineTranslation(); }
export function offlineTranslationStatus(options) { return translation().status(options); }
export function downloadOfflineTranslation(options) { return translation().download(options); }
export function translateOffline(text, options) { return translation().translate(text, options); }
