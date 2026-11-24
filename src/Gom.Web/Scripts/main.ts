import { createApi } from './api.js';
import { mountAdd } from './pages/add.js';
import { mountFriends } from './pages/friends.js';
import { mountChat, type ChatDeps } from './pages/chat.js';
import { createLive } from './chat/live.js';
import { mountJoin } from './pages/join.js';
import { mountOwner } from './pages/owner.js';
import { mountReset } from './pages/reset.js';
import { mountSettings } from './pages/settings.js';
import { mountSignIn } from './pages/signin.js';
import { applyScheme, storedScheme } from './theme.js';

const root = document.getElementById('app');
const api = createApi();
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
});

if (root) {
  switch (document.body.dataset['page']) {
    case 'signin': mountSignIn(root, api, go, new URLSearchParams(location.search).get('next')); break;
    case 'join': mountJoin(root, api, go, token); break;
    case 'reset': mountReset(root, api, go, token); break;
    case 'home': mountChat(root, api, go, chatDeps()); break;
    case 'settings': mountSettings(root, api, go); break;
    case 'friends': mountFriends(root, api, go, location.origin); break;
    case 'add': mountAdd(root, api, go, token); break;
    case 'owner': mountOwner(root, api, go, location.origin); break;
  }
}
