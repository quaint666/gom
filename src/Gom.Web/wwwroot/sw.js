// Gom's service worker. It shows a "no connection" page when a page cannot be fetched, it keeps the scripts, styles, fonts,
// icons and stickers so the app opens faster, and it shows notifications. It never touches /api or the socket, and never stores a page,
// so nothing of what people wrote is kept here. Pages load fingerprinted copies of the script and the styles (a new version has a
// new name); fonts, icons and the plain names are not fingerprinted. Either way a file is fetched from the network first and the
// stored copy is only the fallback: an update arrives as soon as there is a connection.
const STATIC = 'gom-static-v1';
const OFFLINE = '/offline.html';
const KEPT = [OFFLINE, '/offline.js', '/css/gom.css', '/icon.svg'];
const isStatic = (url) => /^\/(js|css|fonts|icons|stickers)\//.test(url.pathname) || ['/icon.svg', '/offline.html', '/offline.js'].includes(url.pathname);
// A connection that hangs (Wi-Fi without internet) never fails, and an app opened from the Home Screen would show its splash
// screen for as long as the browser waits. A page that has not come in this long gets the "no connection" page instead.
const WAIT_MS = 8000;
const KEPT_WAIT_MS = 3000;
const giveUp = (ms) => new Promise((_, fail) => setTimeout(fail, ms));

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
    event.respondWith(Promise.race([fetch(request), giveUp(WAIT_MS)]).catch(() => caches.match(OFFLINE)));
    return;
  }
  if (isStatic(url)) {
    const network = fetch(request).then((response) => {
      if (response.ok) { const copy = response.clone(); void caches.open(STATIC).then((cache) => cache.put(request, copy)); }
      return response;
    });
    // With a stored copy, a hanging network is not waited for long: the "no connection" page needs its styles to show at all.
    event.respondWith(caches.match(request).then((kept) => (kept
      ? Promise.race([network, giveUp(KEPT_WAIT_MS)]).catch(() => kept)
      : network.catch(() => Response.error()))));
  }
});

// A notification. The server encrypts for this browser only, and writes the words (who and where, the kind of message, and its
// words where this device chose them). Every push must show something, or a browser may take the permission away, so a push that
// cannot be read still says "Tin nhắn mới". One conversation keeps one notification: a new one replaces it (tag) and makes a
// sound again (renotify).
const ICON = '/icons/icon-192.png';
self.addEventListener('push', (event) => {
  let note = {};
  try { note = event.data ? event.data.json() : {}; } catch { note = {}; }
  const conversation = typeof note.c === 'string' ? note.c : '';
  const message = typeof note.m === 'string' && /^[0-9a-f-]{36}$/i.test(note.m) ? note.m : '';
  const tag = conversation || 'gom';
  const title = typeof note.title === 'string' && note.title ? note.title : 'Gom';
  const body = typeof note.body === 'string' && note.body ? note.body : 'Tin nhắn mới';
  const url = /^[0-9a-f-]{36}$/i.test(conversation) ? `/c/${conversation}` : '/';
  const data = message ? { url, m: message } : { url };
  if (note.recalled === true) {
    // A message was taken back. Its notification, if still shown, now says so, without a sound. A newer notification of the
    // conversation stays as it is (shown again, unchanged and silent, since every push must show something); with none, the quiet line.
    event.waitUntil((async () => {
      const shown = (await self.registration.getNotifications({ tag }))[0];
      if (shown && shown.data?.m !== message) {
        return self.registration.showNotification(shown.title, { body: shown.body, tag, renotify: false, silent: true, icon: ICON, data: shown.data });
      }
      return self.registration.showNotification(title, { body, tag, renotify: false, silent: true, icon: ICON, data });
    })());
    return;
  }
  event.waitUntil(self.registration.showNotification(title, { body, tag, renotify: true, icon: ICON, data }));
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
