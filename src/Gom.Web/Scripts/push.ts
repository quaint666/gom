/**
 * The browser's side of notifications: whether this device can get them, asking for permission, and the subscription the browser's
 * push service hands out. Settings and the chat page use it through this small interface, so a test can put a fake in its place.
 */

/** What this device can do: get notifications, not at all, or (an iPhone or iPad) only once Gom is on the Home Screen. */
export type PushSupport = 'ok' | 'unsupported' | 'needs-home-screen';

/** A subscription as the server wants it: the push service's address and the browser's keys. */
export interface DeviceSubscription { endpoint: string; keys: { p256dh: string; auth: string } }

export interface Pusher {
  support(): PushSupport;
  /** The browser's answer so far: not asked ('default'), allowed, or blocked (then only the browser's settings can change it). */
  permission(): NotificationPermission;
  /**
   * Asks for permission (it must run in the handler of a tap), then subscribes with the server's key. 'no-service': the browser has no
   * push service to subscribe with (Helium and ungoogled-chromium removed Google's, and theirs never answers; Brave with Google's
   * switched off says "push service error").
   */
  subscribe(key: string): Promise<DeviceSubscription | 'denied' | 'failed' | 'no-service'>;
  /** The subscription this browser has now, if any. */
  current(): Promise<DeviceSubscription | null>;
  unsubscribe(): Promise<void>;
  /** Closes the notifications of one conversation, when it is opened on this device. */
  clear(conversation: string): void;
}

const toKey = (base64url: string): Uint8Array<ArrayBuffer> => {
  const s = atob(base64url.replace(/-/g, '+').replace(/_/g, '/'));
  const bytes = new Uint8Array(new ArrayBuffer(s.length));
  for (let i = 0; i < s.length; i++) bytes[i] = s.charCodeAt(i);
  return bytes;
};

const asDevice = (s: PushSubscription | null): DeviceSubscription | null => {
  const j = s?.toJSON();
  return j?.endpoint && j.keys?.['p256dh'] && j.keys['auth'] ? { endpoint: j.endpoint, keys: { p256dh: j.keys['p256dh'], auth: j.keys['auth'] } } : null;
};

/** Said when the browser has no push service of its own: nothing Gom can change, so it says which browsers can. */
export const NO_PUSH_SERVICE = 'Trình duyệt này không có dịch vụ thông báo đẩy, nên Gom không gửi thông báo đến đây được. Một số trình duyệt dựa trên Chromium (như Helium) đã bỏ dịch vụ này; Chrome, Edge và Firefox đều nhận được.';

/** How long a browser's push service gets to hand out a subscription. One with none never answers at all. */
export const SUBSCRIBE_WAIT_MS = 20_000;

/** The real one, on the page's own `window`. */
export function createPusher(w: Window & typeof globalThis = window): Pusher {
  const nav = w.navigator;
  const registration = async (): Promise<ServiceWorkerRegistration | undefined> => ('serviceWorker' in nav ? nav.serviceWorker.getRegistration() : undefined);
  return {
    support() {
      // An iPhone or iPad (iPadOS says it is a Mac, but has a touch screen) gets push only for an app opened from the Home Screen.
      const apple = /iPhone|iPad|iPod/.test(nav.userAgent) || (nav.platform === 'MacIntel' && nav.maxTouchPoints > 1);
      const standalone = w.matchMedia?.('(display-mode: standalone)').matches || (nav as Navigator & { standalone?: boolean }).standalone === true;
      if (apple && !standalone) return 'needs-home-screen';
      return 'serviceWorker' in nav && 'PushManager' in w && 'Notification' in w ? 'ok' : 'unsupported';
    },
    permission: () => ('Notification' in w ? w.Notification.permission : 'denied'),
    async subscribe(key) {
      try {
        if (await w.Notification.requestPermission() !== 'granted') return 'denied';
        const reg = await nav.serviceWorker.ready;
        const wanted = toKey(key);
        // A subscription made with another server key (the keys were changed) cannot be reused, and blocks a new one until it goes.
        const old = await reg.pushManager.getSubscription();
        const oldKey = old?.options.applicationServerKey;
        if (old && !(oldKey && new Uint8Array(oldKey).every((b, i) => b === wanted[i]) && oldKey.byteLength === wanted.length)) await old.unsubscribe();
        let wait = 0;
        const silent = new Promise<'no-service'>((resolve) => { wait = w.setTimeout(() => resolve('no-service'), SUBSCRIBE_WAIT_MS); });
        const made = await Promise.race([reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: wanted }), silent]);
        w.clearTimeout(wait);
        return made === 'no-service' ? made : asDevice(made) ?? 'failed';
      } catch (e) { return /push service/i.test(String((e as { message?: unknown } | null)?.message ?? '')) ? 'no-service' : 'failed'; }
    },
    async current() {
      try { return asDevice(await (await registration())?.pushManager.getSubscription() ?? null); } catch { return null; }
    },
    async unsubscribe() {
      try { await (await (await registration())?.pushManager.getSubscription())?.unsubscribe(); } catch { /* the server forgets it anyway */ }
    },
    clear(conversation) {
      void registration().then((reg) => reg?.getNotifications({ tag: conversation }))
        .then((shown) => { for (const n of shown ?? []) n.close(); }).catch(() => undefined);
    },
  };
}
