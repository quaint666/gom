import { createApi } from './api.js';
import { mountAdd } from './pages/add.js';
import { mountGroup } from './pages/group.js';
import { mountNewGroup } from './pages/newgroup.js';
import { mountFriends } from './pages/friends.js';
import { mountChat, type ChatDeps, type OutboxItem } from './pages/chat.js';
import { createInstaller } from './install.js';
import { createLive } from './chat/live.js';
import { mountJoin } from './pages/join.js';
import { mountReset } from './pages/reset.js';
import { mountSettings } from './pages/settings.js';
import { createPusher } from './push.js';
import { mountSignIn } from './pages/signin.js';
import { mountSignUp } from './pages/signup.js';
import { language, pageTitle, rememberLanguage } from './say.js';
import { BLACK_STORAGE_KEY, PALETTE_STORAGE_KEY, applyBlack, applyPalette, applyScheme, isPalette, isPattern, rememberPattern, storedBlack, storedPalette, storedScheme, syncThemeColor } from './theme.js';
import { showUpdateBar, watchForUpdate } from './update.js';

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

const pusher = createPusher();
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

const chatDeps = (): ChatDeps => ({
  clearNotifications: (conversation) => pusher.clear(conversation),
  pusher,
  prepareVideo,
  live: (handlers) => createLive(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/ws`, handlers),
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
});

if (root) {
  switch (document.body.dataset['page']) {
    case 'signin': mountSignIn(root, api, go, new URLSearchParams(location.search).get('next')); break;
    case 'join': mountJoin(root, api, go, token); break;
    case 'signup': mountSignUp(root, api, go); break;
    case 'reset': mountReset(root, api, go, token); break;
    case 'home': mountChat(root, api, go, chatDeps()); break;
    case 'settings': mountSettings(root, api, go, installer, pusher, { path: () => location.pathname, push: (path) => history.pushState(null, '', path), replace: (path) => history.replaceState(null, '', path), onPop: (callback) => addEventListener('popstate', callback) }); break;
    case 'friends': mountFriends(root, api, go, location.origin); break;
    case 'add': mountAdd(root, api, go, token); break;
    case 'newgroup': mountNewGroup(root, api, go, new URLSearchParams(location.search).get('with')); break;
    case 'group': mountGroup(root, api, go, token); break;
  }
}

// A new version deployed while this page is open: say so (never reload by itself). The page asks about the script it runs from.
const updateBar = document.createElement('div');
document.body.append(updateBar);
const ownScript = new URL(import.meta.url).pathname;
watchForUpdate({
  probe: async () => {
    try {
      const response = await fetch(ownScript, { method: 'HEAD', cache: 'no-store', credentials: 'omit' });
      return { status: response.status, etag: response.headers.get('etag') };
    } catch { return null; }
  },
  now: () => Date.now(),
  every: (fn, ms) => { window.setInterval(fn, ms); },
  onShown: (fn) => { document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') fn(); }); },
  announce: () => showUpdateBar(updateBar, () => location.reload()),
});

// The app can be added to a home screen, and shows a "no connection" page instead of the browser's own. Browsers only allow this on https and on localhost.
if ('serviceWorker' in navigator) void navigator.serviceWorker.register('/sw.js').catch(() => undefined);
