const scope = self.registration.scope;
const prefix = 'tuci:web:' + new URL(scope).pathname + ':';
const currentCache = prefix + "__VERSION__";
const files = "__ASSETS__";
const indexURL = new URL('index.html', scope).href;
const assetURLs = new Set(Object.keys(files).map(path => new URL(path, scope).href));
const fontPrefix = 'tuci:fonts:' + new URL(scope).pathname + ':';
const fontCache = fontPrefix + "__FONT_VERSION__";
const fontFiles = "__FONT_ASSETS__";
const fontURLs = new Map(Object.entries(fontFiles).map(([path,info]) => [new URL(path,scope).href,info]));
const fontRequests = new Map();

async function verifiedFont(response, info) {
  if (!response.ok || response.redirected || response.type === 'opaque' ||
      Number(response.headers.get('Content-Length')) > info.bytes) throw new Error('Invalid font response');
  const bytes = await response.arrayBuffer();
  if (bytes.byteLength !== info.bytes) throw new Error('Invalid font length');
  const hash = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',bytes)),n=>n.toString(16).padStart(2,'0')).join('');
  if (hash !== info.sha256) throw new Error('Invalid font digest');
  return new Response(bytes,{headers:{'Content-Type':'font/woff2','Content-Length':String(info.bytes),'X-Tuci-SHA256':hash}});
}
async function loadFont(url, info) {
  let cache;
  try { cache = await caches.open(fontCache); } catch { /* Online type remains usable without storage. */ }
  if (cache) {
    const stored = await cache.match(url);
    if (stored) {
      try { return await verifiedFont(stored,info); }
      catch { try { await cache.delete(url); } catch {} }
    }
  }
  const response = await verifiedFont(await fetch(url,{credentials:'omit',mode:'same-origin',
    cache:'no-store',redirect:'error',referrerPolicy:'no-referrer'}),info);
  if (cache) try { await cache.put(url,response.clone()); } catch { /* Quota cannot hide online text. */ }
  return response;
}

self.addEventListener('install', event => {
  // Activate only a complete, verified release, even when another old tab is open.
  const requests = Object.entries(files).map(([path,integrity]) =>
    new Request(new URL(path,scope), { cache:'reload', integrity, credentials:'same-origin' }));
  event.waitUntil((async () => {
    const cache = await caches.open(currentCache);
    await cache.addAll(requests);
    await self.skipWaiting();
  })());
});
self.addEventListener('activate', event => {
  event.waitUntil((async () => {
    const old = (await caches.keys()).filter(name => name.startsWith(prefix) && name !== currentCache ||
      name.startsWith(fontPrefix) && name !== fontCache);
    await Promise.all(old.map(name => caches.delete(name)));
    await self.clients.claim();
  })());
});
self.addEventListener('fetch', event => {
  if (event.request.method !== 'GET') return;
  const url = new URL(event.request.url);
  if (url.origin !== self.location.origin) return;
  const home = url.pathname === new URL(scope).pathname || url.pathname === new URL(indexURL).pathname;
  const file = event.request.mode === 'navigate' && home ? indexURL : url.href;
  if (!assetURLs.has(file)) {
    const info = fontURLs.get(url.href);
    if (!info) return;
    if (!fontRequests.has(url.href)) {
      const pending = loadFont(url.href,info).finally(()=>fontRequests.delete(url.href));
      fontRequests.set(url.href,pending);
    }
    event.respondWith(fontRequests.get(url.href).then(response=>response.clone()));
    return;
  }
  event.respondWith((async () => {
    const cache = await caches.open(currentCache);
    return (await cache.match(file)) || fetch(event.request);
  })());
});
