import { createApi } from './api.js';
import { mountAdd } from './pages/add.js';
import { mountGroup } from './pages/group.js';
import { mountNewGroup } from './pages/newgroup.js';
import { mountFriends } from './pages/friends.js';
import { mountChat, type ChatDeps, type OutboxItem } from './pages/chat.js';
import { createInstaller } from './install.js';
import { createLive } from './chat/live.js';
import { mountJoin } from './pages/join.js';
import { mountOwner } from './pages/owner.js';
import { mountReset } from './pages/reset.js';
import { mountSettings } from './pages/settings.js';
import { mountSignIn } from './pages/signin.js';
import { applyScheme, storedScheme } from './theme.js';
import { showUpdateBar, watchForUpdate } from './update.js';

const root = document.getElementById('app');
const api = createApi();
// The browser offers to install the app once, early: listen on every page so the settings page can use it.
const installer = createInstaller(window);
const go = (path: string): void => location.assign(path);
// /join/<token> and /reset/<token>
const token = decodeURIComponent(location.pathname.split('/')[2] ?? '');

// Another tab changed the colour scheme: follow it.
addEventListener('storage', (e) => { if (e.key === 'color-scheme') applyScheme(storedScheme()); });

const chatDeps = (): ChatDeps => ({
  live: (handlers) => createLive(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/ws`, handlers),
  nav: { path: () => location.pathname, push: (path) => history.pushState(null, '', path), onPop: (callback) => addEventListener('popstate', callback) },
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
    case 'reset': mountReset(root, api, go, token); break;
    case 'home': mountChat(root, api, go, chatDeps()); break;
    case 'settings': mountSettings(root, api, go, installer); break;
    case 'friends': mountFriends(root, api, go, location.origin); break;
    case 'add': mountAdd(root, api, go, token); break;
    case 'newgroup': mountNewGroup(root, api, go); break;
    case 'group': mountGroup(root, api, go, token); break;
    case 'owner': mountOwner(root, api, go, location.origin); break;
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
