import { createApi } from './api.js';
import { mountAdd } from './pages/add.js';
import { mountGroup } from './pages/group.js';
import { mountNewGroup } from './pages/newgroup.js';
import { mountFriends } from './pages/friends.js';
import { mountChat, type ChatDeps, type OutboxItem } from './pages/chat.js';
import { createInstaller } from './install.js';
import { createLive } from './chat/live.js';
import { limits } from './limits.js';
import { mountJoin } from './pages/join.js';
import { mountReset } from './pages/reset.js';
import { mountPrivacy } from './pages/privacy.js';
import { mountSettings } from './pages/settings.js';
import { appPort, createAppPusher, type AppPusher } from './app.js';
import { createCalls, type Calls, type PeerLike } from './calls/call.js';
import { createRing } from './calls/ring.js';
import { createPusher, type Pusher } from './push.js';
import { attachPull, canPullList } from './pull.js';
import { mountSignIn } from './pages/signin.js';
import { mountSignUp } from './pages/signup.js';
import { language, pageTitle, rememberLanguage } from './say.js';
import { BLACK_STORAGE_KEY, PALETTE_STORAGE_KEY, applyBlack, applyPalette, applyScheme, isPalette, isPattern, rememberPattern, storedBlack, storedPalette, storedScheme, syncThemeColor } from './theme.js';
import { showUpdateBar, versionOf, watchForUpdate } from './update.js';

const root = document.getElementById('app');
// A request to join that is still waiting is sent home, where the chat page shows its waiting screen.
// The person's language, when it is not this page's, is remembered and the page loaded again in it.
const api = createApi(undefined, undefined, {
  onPending: () => { if (location.pathname !== '/') location.assign('/'); },
  onLanguage: (chosen) => { rememberLanguage(chosen); location.reload(); },
  // The person's colour, when this device had another: drawn at once, and remembered for the next page.
  onPalette: (chosen) => { if (isPalette(chosen) && chosen !== storedPalette()) applyPalette(chosen); },
  // The background is read by the chat page as it draws, which comes after this answer.
  onPattern: (chosen) => { if (isPattern(chosen)) rememberPattern(chosen); },
  // The server has moved past this page's code: reload, at once if that loses nothing (the watcher, set up below, decides).
  onTooOld: () => updates.tooOld(),
});
syncThemeColor();
document.documentElement.lang = language;
document.title = pageTitle(document.body.dataset['page'] ?? '');
// The browser offers to install the app once, early: listen on every page so the settings page can use it.
const installer = createInstaller(window);
const go = (path: string): void => location.assign(path);
// /join/<token> and /reset/<token>
const token = decodeURIComponent(location.pathname.split('/')[2] ?? '');

// Another tab changed the colour scheme: follow it.
addEventListener('storage', (e) => {
  if (e.key === 'color-scheme') applyScheme(storedScheme());
  if (e.key === PALETTE_STORAGE_KEY) applyPalette(storedPalette());
  if (e.key === BLACK_STORAGE_KEY) applyBlack(storedBlack());
});

// A new version deployed while this page is open: move to it when that loses nothing, otherwise say so (see watchForUpdate).
const updateBar = document.createElement('div');
document.body.append(updateBar);
const ownScript = new URL(import.meta.url).pathname;
/** True while a reload would lose something on this page; the chat page hands over its own question (holdReload). */
let pageBusy = (): boolean => false;
/** A field being filled in: focused and not empty. The message field does not count: its words are kept across a reload. */
const fieldInUse = (): boolean => {
  const el = document.activeElement;
  const typed = el instanceof HTMLTextAreaElement || (el instanceof HTMLInputElement && ['text', 'search', 'password', 'email', 'url', 'tel', 'number', 'date'].includes(el.type));
  return typed && !el.hasAttribute('data-kept') && el.value !== '';
};
const RELOADED_AT = 'gom-reloaded-at';
const updates = watchForUpdate({
  probe: async () => {
    try {
      const response = await fetch(ownScript, { method: 'HEAD', cache: 'no-store', credentials: 'omit' });
      return { status: response.status, etag: response.headers.get('etag') };
    } catch { return null; }
  },
  now: () => Date.now(),
  every: (fn, ms) => { window.setInterval(fn, ms); },
  onShown: (fn) => { document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') fn(); }); },
  onHidden: (fn) => { document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'hidden') fn(); }); },
  visible: () => document.visibilityState === 'visible',
  busy: () => pageBusy() || fieldInUse(),
  announce: (must) => showUpdateBar(updateBar, () => location.reload(), must),
  reload: () => location.reload(),
  reloadedAt: {
    get: () => { try { const at = Number(sessionStorage.getItem(RELOADED_AT)); return at > 0 ? at : null; } catch { return null; } },
    set: (at) => { try { sessionStorage.setItem(RELOADED_AT, String(at)); } catch { /* then a loop is stopped only by the page loading the new version */ } },
  },
  own: versionOf(ownScript),
});

/** "Tải lại Gom": the service worker drops every file it keeps and fetches them again, then the page reloads. Without one, it just reloads. */
async function reloadFresh(): Promise<void> {
  try {
    if ('serviceWorker' in navigator) {
      await (await navigator.serviceWorker.getRegistration())?.update();
      const worker = navigator.serviceWorker.controller;
      if (worker !== null) {
        await new Promise<void>((done) => {
          const channel = new MessageChannel();
          channel.port1.onmessage = () => done();
          worker.postMessage('refresh', [channel.port2]);
          window.setTimeout(done, 5000);
        });
      }
    }
  } catch { /* reload anyway */ }
  location.reload();
}

/**
 * Inside the Android app, notifications are the app's (FCM); in a browser, the browser's. The app's registration is sent to the
 * server whenever it is new to this tab, so an id FCM changed while the app was closed is put right the next time it opens.
 */
const port = appPort();
let appPusher: AppPusher | undefined;
const pusher: Pusher = port === undefined ? createPusher() : (() => {
  const l = limits();
  const app = createAppPusher(port, window, Math.max(l.imageBytes, l.animatedBytes));
  app.onRegistration((sub) => void (async () => {
    const KEY = 'gom.appRegistration';
    if (!('app' in sub)) return;
    try { if (sessionStorage.getItem(KEY) === sub.app) return; } catch { /* not remembered: sent again */ }
    const state = await api.pushState();
    if (!state.ok) return;
    const result = await api.setPush(sub, state.data.level);
    if (result.ok) try { sessionStorage.setItem(KEY, sub.app); } catch { /* sent again next time */ }
  })());
  appPusher = app;
  return app;
})();
/**
 * The video converter, fetched the first time a video is picked: its address is on the page (`data-video-script`, the server's
 * fingerprinted name), so a new version is a new name. Typed from the module itself; nothing of it is bundled here.
 */
const prepareVideo: ChatDeps['prepareVideo'] = async (file, onProgress, maxBytes) => {
  const address = document.body.dataset['videoScript'];
  if (!address) return null;
  const converter = await import(address) as typeof import('./video.js');
  return converter.prepareVideo(file, onProgress, maxBytes);
};

/**
 * Voice calls, in a browser that has WebRTC and a microphone API (docs/voice-calls.md, 2b). Only the chat page takes calls: it tells
 * the server so on its socket. In the Android app the app makes and takes them natively, and the page's buttons only ask it to.
 */
const webCalls: Calls | undefined = port === undefined && typeof RTCPeerConnection === 'function' && navigator.mediaDevices?.getUserMedia !== undefined
  && document.body.dataset['page'] === 'home' ? (() => {
    const host = document.createElement('div');
    host.className = 'call-host';
    document.body.append(host);
    const sound = document.createElement('audio');
    sound.autoplay = true;
    host.after(sound);
    const ownTitle = document.title;
    const askBeforeLeaving = (e: BeforeUnloadEvent) => { e.preventDefault(); };
    const made = createCalls({
      api, host,
      peer: (config) => new RTCPeerConnection(config) as unknown as PeerLike,
      mic: () => navigator.mediaDevices.getUserMedia({ audio: true }),
      play: (stream) => { sound.srcObject = stream; if (stream !== null) void sound.play().catch(() => undefined); },
      ring: createRing(),
      title: (text) => { document.title = text ?? ownTitle; },
      guardLeaving: (on) => { if (on) addEventListener('beforeunload', askBeforeLeaving); else removeEventListener('beforeunload', askBeforeLeaving); },
      phone: () => matchMedia('(pointer: coarse)').matches,
      now: () => performance.now(),
      timer: { set: (fn, ms) => window.setTimeout(fn, ms), clear: (id) => window.clearTimeout(id) },
      newId: () => crypto.randomUUID(),
    });
    addEventListener('pagehide', () => made.leaving());
    return made;
  })() : undefined;
const calls: Calls | undefined = webCalls ?? appPusher?.calls;

const chatDeps = (): ChatDeps => ({
  clearNotifications: (conversation) => pusher.clear(conversation),
  pusher,
  ...(appPusher === undefined ? {} : (({ onKeyboardImage }) => ({ keyboardImages: onKeyboardImage }))(appPusher)),
  prepareVideo,
  ...(calls === undefined ? {} : { calls }),
  live: (handlers) => createLive(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/ws?page=${limits().pageVersion}${webCalls === undefined ? '' : '&calls=1'}`,
    { ...handlers, version: (v) => updates.heard(v), tooOld: () => { handlers.tooOld?.(); updates.tooOld(); } }),
  nav: { path: () => location.pathname, push: (path) => history.pushState(null, '', path), replace: (path) => history.replaceState(null, '', path), back: () => history.back(), onPop: (callback) => addEventListener('popstate', callback) },
  now: () => new Date(),
  newId: () => crypto.randomUUID(),
  coarsePointer: () => matchMedia('(pointer: coarse)').matches,
  visible: () => document.visibilityState === 'visible',
  timer: { set: (fn, ms) => window.setTimeout(fn, ms), clear: (id) => window.clearTimeout(id) },
  outbox: (gomId) => {
    const key = `gom-outbox:${gomId}`;
    return {
      load: () => {
        try { const parsed: unknown = JSON.parse(localStorage.getItem(key) ?? '[]'); return Array.isArray(parsed) ? (parsed as OutboxItem[]) : []; } catch { return []; }
      },
      // Storage can be full or switched off (private browsing): then the queue still works until the page closes.
      save: (items) => { try { if (items.length === 0) localStorage.removeItem(key); else localStorage.setItem(key, JSON.stringify(items)); } catch { /* keep going without it */ } },
    };
  },
  // This tab only (sessionStorage): a reload keeps the words, closing the tab drops them, so nothing typed stays on a shared device.
  drafts: (gomId) => {
    const key = (conversation: string): string => `gom-draft:${gomId}:${conversation}`;
    return {
      load: (conversation) => { try { return sessionStorage.getItem(key(conversation)) ?? ''; } catch { return ''; } },
      save: (conversation, text) => { try { if (text === '') sessionStorage.removeItem(key(conversation)); else sessionStorage.setItem(key(conversation), text); } catch { /* kept until the page closes */ } },
    };
  },
  holdReload: (busy) => { pageBusy = busy; },
});

if (root) {
  switch (document.body.dataset['page']) {
    case 'signin': mountSignIn(root, api, go, new URLSearchParams(location.search).get('next')); break;
    case 'join': mountJoin(root, api, go, token); break;
    case 'signup': mountSignUp(root, api, go); break;
    case 'reset': mountReset(root, api, go, token); break;
    case 'home': {
      mountChat(root, api, go, chatDeps());
      // Pull the list down on a phone to reload. Only the chat page's own question decides (not a focused field: the search box
      // being filled in is no reason to refuse a pull the person asked for).
      const pullHost = document.createElement('div');
      document.body.append(pullHost);
      attachPull({
        area: root, host: pullHost,
        canStart: (target) => canPullList(target, root, (el) => getComputedStyle(el).display !== 'none'),
        busy: () => pageBusy(),
        reload: () => location.reload(),
        later: (fn, ms) => { window.setTimeout(fn, ms); },
      });
      break;
    }
    case 'settings': mountSettings(root, api, go, installer, pusher, { path: () => location.pathname, push: (path) => history.pushState(null, '', path), replace: (path) => history.replaceState(null, '', path), onPop: (callback) => addEventListener('popstate', callback) }, { version: document.body.dataset['version'] || null, reload: () => { void reloadFresh(); } }); break;
    case 'friends': mountFriends(root, api, go, location.origin); break;
    case 'add': mountAdd(root, api, go, token); break;
    case 'newgroup': mountNewGroup(root, api, go, new URLSearchParams(location.search).get('with')); break;
    case 'group': mountGroup(root, api, go, token); break;
    case 'privacy': mountPrivacy(root, document.body.dataset['operator'] ?? '', api); break;
  }
}

// The app can be added to a home screen, and shows a "no connection" page instead of the browser's own. Browsers only allow this on https and on localhost.
if ('serviceWorker' in navigator) void navigator.serviceWorker.register('/sw.js').catch(() => undefined);
