import type { DeviceSubscription, Pusher } from './push.js';

/**
 * The page's side of the Android app (docs/voice-calls.md, decisions 31 and 43). The app shows Gom in a web view and puts a
 * `gomApp` object on Gom's own pages only (androidx.webkit's web message listener, given Gom's origin); messages both ways are
 * JSON strings, with a protocol version from the start, as the call protocol has. Notifications in the app come by FCM, so the app
 * gets a {@link Pusher} of its own: Settings and the chat page use it exactly as they use the browser's.
 *
 * Protocol 1:
 * - page → app: `{t: 'hello'}`, `{t: 'enable'}` (ask Android's permission and register with FCM), `{t: 'disable'}`,
 *   `{t: 'clear', c}` (close one conversation's notifications), `{t: 'theme', bar, page}` (the colours behind the system bars).
 * - app → page: `{t: 'hello', v, app, push, permission, fid?}`, then `{t: 'enabled', fid}`, `{t: 'denied'}` or `{t: 'failed'}`
 *   for an `enable`, and `{t: 'fid', fid}` when FCM gives the app a new installation id.
 */
export const APP_PROTOCOL = 1;

/** The object the app puts on the page: it takes strings, and hands the app's messages to `onmessage`. */
export interface AppPort {
  postMessage(message: string): void;
  onmessage: ((event: { data: unknown }) => void) | null;
}

/** What the app says of itself when the page says hello. */
export interface AppHello {
  v: number;
  /** The app's version, as the Play Store shows it. */
  app: string;
  /** Whether this build can get notifications at all (its Firebase settings were given when it was built). */
  push: boolean;
  /** Android's notification permission for the app, in the browser's words. */
  permission: NotificationPermission;
  /** Its FCM installation id, once it has registered. */
  fid?: string;
}

/** The app's object, when this page runs inside it. */
export const appPort = (w: Window = window): AppPort | undefined => {
  const port = (w as Window & { gomApp?: unknown }).gomApp;
  return typeof port === 'object' && port !== null && typeof (port as AppPort).postMessage === 'function' ? port as AppPort : undefined;
};

type FromApp = { t: 'hello' } & AppHello | { t: 'enabled'; fid: string } | { t: 'denied' } | { t: 'failed' } | { t: 'fid'; fid: string };

const parse = (data: unknown): FromApp | null => {
  try {
    const m = JSON.parse(typeof data === 'string' ? data : '') as { t?: unknown; fid?: unknown };
    if (typeof m !== 'object' || m === null || typeof m.t !== 'string') return null;
    if ((m.t === 'enabled' || m.t === 'fid') && (typeof m.fid !== 'string' || m.fid === '')) return null;
    return m as FromApp;
  } catch { return null; }
};

/** How long the app gets to answer "enable": Android's permission question waits for the person, so it is long. */
export const ENABLE_WAIT_MS = 120_000;

export interface AppPusher extends Pusher {
  /** Called with the app's registration whenever it may be new: the hello that carries one, and each new id from FCM. */
  onRegistration(callback: (subscription: DeviceSubscription) => void): void;
}

/**
 * The app's notifications behind the browser's interface: subscribing asks the app (Android's permission, then FCM), and the
 * "subscription" is the app's installation id, which the server keeps as the session's app (`PUT /api/push/fcm`).
 */
export function createAppPusher(port: AppPort, w: Window & typeof globalThis = window): AppPusher {
  let hello: AppHello | null = null;
  let fid: string | undefined;
  let waiting: ((m: FromApp) => void) | null = null;
  const registered: Array<(s: DeviceSubscription) => void> = [];
  const tell = (id: string): void => { for (const r of registered) r({ app: id }); };
  const send = (m: object): void => { try { port.postMessage(JSON.stringify(m)); } catch { /* the app is going away */ } };

  port.onmessage = (event) => {
    const m = parse(event.data);
    if (m === null) return;
    if (m.t === 'hello') {
      hello = m;
      fid = m.fid;
      if (fid) tell(fid);
    } else if (m.t === 'fid') {
      fid = m.fid;
      tell(fid);
    } else if (waiting !== null) {
      const answer = waiting;
      waiting = null;
      answer(m);
    }
  };
  send({ t: 'hello', v: APP_PROTOCOL });
  watchTheme(send, w);

  return {
    // Until the app has said hello, it is assumed able: a build with no Firebase settings says so.
    support: () => (hello === null || hello.push ? 'ok' : 'unsupported'),
    permission: () => hello?.permission ?? 'default',
    async subscribe() {
      const answer = await new Promise<FromApp | null>((resolve) => {
        waiting = resolve;
        w.setTimeout(() => { if (waiting === resolve) { waiting = null; resolve(null); } }, ENABLE_WAIT_MS);
        send({ t: 'enable' });
      });
      if (answer?.t === 'enabled') {
        fid = answer.fid;
        if (hello) hello = { ...hello, permission: 'granted' };
        return { app: answer.fid };
      }
      if (answer?.t === 'denied') {
        if (hello) hello = { ...hello, permission: 'denied' };
        return 'denied';
      }
      return 'failed';
    },
    current: async () => (fid ? { app: fid } : null),
    async unsubscribe() { fid = undefined; send({ t: 'disable' }); },
    clear(conversation) { send({ t: 'clear', c: conversation }); },
    onRegistration(callback) {
      registered.push(callback);
      if (fid) callback({ app: fid });
    },
  };
}

/**
 * The app draws Android's status and navigation bars over the page's colours: the top takes the page's main colour (the
 * `theme-color` the browser's bar takes), the bottom the page's background. Sent now and whenever either changes.
 */
function watchTheme(send: (m: object) => void, w: Window & typeof globalThis): void {
  const doc = w.document;
  let last = '';
  const tell = (): void => {
    const bar = doc.querySelector('meta[name="theme-color"]')?.getAttribute('content') ?? '';
    const page = w.getComputedStyle(doc.body).backgroundColor;
    const now = `${bar}|${page}`;
    if (now === last) return;
    last = now;
    send({ t: 'theme', bar, page });
  };
  tell();
  const watch = new w.MutationObserver(tell);
  watch.observe(doc.head, { subtree: true, attributes: true, attributeFilter: ['content'] });
  watch.observe(doc.documentElement, { attributes: true });
  w.matchMedia?.('(prefers-color-scheme: dark)').addEventListener?.('change', tell);
}
