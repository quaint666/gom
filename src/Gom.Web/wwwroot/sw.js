// Gom's service worker. It shows a "no connection" page when a page cannot be fetched, it keeps the scripts, styles, fonts and
// icons so the app opens faster, and it shows notifications. It never touches /api or the socket, and never stores a page,
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

// A notification. The server encrypts for this browser only, and writes the words (who and where, and the kind of message, never
// its words). Every push must show something, or a browser may take the permission away, so a push that cannot be read still
// says "Tin nhắn mới". One conversation keeps one notification: a new one replaces it (tag) and makes a sound again (renotify).
self.addEventListener('push', (event) => {
  let note = {};
  try { note = event.data ? event.data.json() : {}; } catch { note = {}; }
  const conversation = typeof note.c === 'string' ? note.c : '';
  event.waitUntil(self.registration.showNotification(typeof note.title === 'string' && note.title ? note.title : 'Gom', {
    body: typeof note.body === 'string' && note.body ? note.body : 'Tin nhắn mới',
    tag: conversation || 'gom',
    renotify: true,
    icon: '/icons/icon-192.png',
    data: { url: /^[0-9a-f-]{36}$/i.test(conversation) ? `/c/${conversation}` : '/' },
  }));
});

// A tap opens its conversation: in a Gom window already open (brought to the front), or a new one.
self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const url = new URL(event.notification.data?.url ?? '/', self.location.origin).href;
  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    const open = windows.find((w) => new URL(w.url).origin === self.location.origin);
    if (open) {
      const shown = await open.focus();
      if (shown.url !== url && 'navigate' in shown) { try { await shown.navigate(url); } catch { /* not controlled yet: stays where it is */ } }
      return;
    }
    await self.clients.openWindow(url);
  })());
});
