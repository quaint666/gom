// Gom's service worker. It does two small things: it shows a "no connection" page when a page cannot be fetched, and it keeps
// the scripts, styles, fonts and icons so the app opens faster. It never touches /api or the socket, and never stores a page,
// so nothing of what people wrote is kept here. Pages load fingerprinted copies of the script and the styles (a new version has a
// new name); fonts, icons and the plain names are not fingerprinted. Either way a file is fetched from the network first and the
// stored copy is only the fallback: an update arrives as soon as there is a connection.
const STATIC = 'gom-static-v1';
const OFFLINE = '/offline.html';
const KEPT = [OFFLINE, '/css/gom.css', '/icon.svg'];
const isStatic = (url) => /^\/(js|css|fonts|icons)\//.test(url.pathname) || url.pathname === '/icon.svg' || url.pathname === '/offline.html';

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(STATIC).then((cache) => cache.addAll(KEPT)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys()
      .then((names) => Promise.all(names.filter((n) => n !== STATIC).map((n) => caches.delete(n))))
      .then(() => self.clients.claim()));
});

self.addEventListener('fetch', (event) => {
  const request = event.request;
  if (request.method !== 'GET') return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin || url.pathname.startsWith('/api/') || url.pathname === '/ws') return;

  if (request.mode === 'navigate') {
    event.respondWith(fetch(request).catch(() => caches.match(OFFLINE)));
    return;
  }
  if (isStatic(url)) {
    event.respondWith(
      fetch(request)
        .then((response) => {
          if (response.ok) { const copy = response.clone(); void caches.open(STATIC).then((cache) => cache.put(request, copy)); }
          return response;
        })
        .catch(() => caches.match(request).then((hit) => hit ?? Response.error())));
  }
});
