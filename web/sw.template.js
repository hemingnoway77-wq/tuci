const scope = self.registration.scope;
const prefix = 'tuci:web:' + new URL(scope).pathname + ':';
const currentCache = prefix + "__VERSION__";
const files = "__ASSETS__";
const indexURL = new URL('index.html', scope).href;
const assetURLs = new Set(Object.keys(files).map(path => new URL(path, scope).href));

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
    const old = (await caches.keys()).filter(name => name.startsWith(prefix) && name !== currentCache);
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
  if (!assetURLs.has(file)) return;
  event.respondWith((async () => {
    const cache = await caches.open(currentCache);
    return (await cache.match(file)) || fetch(event.request);
  })());
});
