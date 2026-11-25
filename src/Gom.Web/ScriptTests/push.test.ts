import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createApi } from '../Scripts/api.js';
import { mountSettings } from '../Scripts/pages/settings.js';
import { createPusher, type Pusher, type PushSupport } from '../Scripts/push.js';
import { fail, fakeApi, flush, ok, text } from './helpers.js';
import swSource from '../wwwroot/sw.js?raw';

let root: HTMLElement;
beforeEach(() => { root = document.createElement('div'); document.body.replaceChildren(root); });

const me = { gomId: 'mom', displayName: 'Mẹ', isOwner: false };
const SUB = { endpoint: 'https://fcm.googleapis.com/fcm/send/abc', keys: { p256dh: 'BKey', auth: 'secret' } };

function fakePusher(over: Partial<{ support: PushSupport; permission: NotificationPermission; subscribe: Pusher['subscribe']; current: Pusher['current'] }> = {}) {
  return {
    support: vi.fn(() => over.support ?? 'ok'),
    permission: vi.fn(() => over.permission ?? 'default'),
    subscribe: vi.fn(over.subscribe ?? (async () => SUB)),
    current: vi.fn(over.current ?? (async () => SUB)),
    unsubscribe: vi.fn(async () => undefined),
    clear: vi.fn(),
  };
}

describe('api: notifications', () => {
  const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

  it('reads the key and the state with GET, registers with PUT and forgets with DELETE', async () => {
    const fetchFn = vi.fn(async (_u: string, init?: RequestInit) => (init?.method === 'GET' ? json({ key: 'K', subscribed: false, level: 'name' }) : new Response(null, { status: 204 })));
    const api = createApi(fetchFn as unknown as typeof fetch);
    expect(await api.pushState()).toEqual({ ok: true, data: { key: 'K', subscribed: false, level: 'name' } });
    await api.setPush(SUB, 'nothing');
    await api.removePush();
    const calls = fetchFn.mock.calls.map(([url, init]) => [url, init?.method, init?.body ? JSON.parse(init.body as string) : undefined]);
    expect(calls).toEqual([['/api/push', 'GET', undefined], ['/api/push', 'PUT', { ...SUB, level: 'nothing' }], ['/api/push', 'DELETE', undefined]]);
  });
});

describe('settings: notifications', () => {
  const state = (subscribed = false, level = 'name') => vi.fn(async () => ok({ key: 'SERVERKEY', subscribed, level }));
  const api = (extra = {}) => fakeApi({ me: vi.fn(async () => ok(me)), sessions: vi.fn(async () => ok([])), pushState: state(), ...extra });
  const card = () => root.querySelector('[data-push]');
  const toggle = async (on: boolean) => {
    const box = root.querySelector<HTMLInputElement>('[data-push-switch]')!;
    box.checked = on;
    box.dispatchEvent(new Event('change'));
    await flush();
  };

  it('shows nothing when the server has push off', async () => {
    mountSettings(root, api({ pushState: vi.fn(async () => fail('unknown', 404)) }), vi.fn(), undefined, fakePusher());
    await flush();
    expect(card()).toBeNull();
  });

  it('turns notifications on: asks the browser with the server key, then registers this device', async () => {
    const a = api();
    const p = fakePusher();
    mountSettings(root, a, vi.fn(), undefined, p);
    await flush();
    expect(root.querySelector<HTMLInputElement>('[data-push-switch]')!.checked).toBe(false);
    expect(root.querySelector('[data-push-level]')).toBeNull();
    expect(text(card()!)).toContain('không bao giờ hiện nội dung');

    await toggle(true);
    expect(p.subscribe).toHaveBeenCalledWith('SERVERKEY');
    expect(a.setPush).toHaveBeenCalledWith(SUB, 'name');
    expect(root.querySelector<HTMLInputElement>('[data-push-switch]')!.checked).toBe(true);
    expect(text(root.querySelector('[data-push-status]')!)).toContain('Đã bật');
    expect(root.querySelector('[data-push-level]')).not.toBeNull();
  });

  it('says so and registers nothing when the person does not allow it, or the browser fails', async () => {
    for (const [answer, words] of [['denied', 'chưa cho phép'], ['failed', 'Không bật được']] as const) {
      const a = api();
      mountSettings(root, a, vi.fn(), undefined, fakePusher({ subscribe: async () => answer }));
      await flush();
      await toggle(true);
      expect(a.setPush).not.toHaveBeenCalled();
      expect(root.querySelector<HTMLInputElement>('[data-push-switch]')!.checked).toBe(false);
      expect(text(root.querySelector('[data-push-status]')!)).toContain(words);
    }
  });

  it('turns notifications off on both sides', async () => {
    const a = api({ pushState: state(true) });
    const p = fakePusher();
    mountSettings(root, a, vi.fn(), undefined, p);
    await flush();
    await toggle(false);
    expect(p.unsubscribe).toHaveBeenCalled();
    expect(a.removePush).toHaveBeenCalled();
    expect(root.querySelector<HTMLInputElement>('[data-push-switch]')!.checked).toBe(false);
    expect(root.querySelector('[data-push-level]')).toBeNull();
  });

  it('changes how much a notification says, for this device', async () => {
    const a = api({ pushState: state(true) });
    mountSettings(root, a, vi.fn(), undefined, fakePusher());
    await flush();
    expect(root.querySelector<HTMLInputElement>('#push-name')!.checked).toBe(true);
    root.querySelector<HTMLInputElement>('#push-nothing')!.dispatchEvent(new Event('change'));
    await flush();
    expect(a.setPush).toHaveBeenCalledWith(SUB, 'nothing');
    expect(root.querySelector<HTMLInputElement>('#push-nothing')!.checked).toBe(true);
  });

  it('says to turn it on again when the browser no longer has the subscription', async () => {
    const a = api({ pushState: state(true) });
    mountSettings(root, a, vi.fn(), undefined, fakePusher({ current: async () => null }));
    await flush();
    root.querySelector<HTMLInputElement>('#push-nothing')!.dispatchEvent(new Event('change'));
    await flush();
    expect(a.setPush).not.toHaveBeenCalled();
    expect(root.querySelector<HTMLInputElement>('[data-push-switch]')!.checked).toBe(false);
    expect(text(root.querySelector('[data-push-status]')!)).toContain('bật lại');
  });

  it('explains the Home Screen on an iPhone, says when a browser cannot, and when the browser blocks Gom', async () => {
    for (const [p, mark] of [
      [fakePusher({ support: 'needs-home-screen' }), '[data-push-home]'],
      [fakePusher({ support: 'unsupported' }), '[data-push-unsupported]'],
      [fakePusher({ permission: 'denied' }), '[data-push-blocked]'],
    ] as const) {
      mountSettings(root, api(), vi.fn(), undefined, p);
      await flush();
      expect(root.querySelector(mark)).not.toBeNull();
      expect(root.querySelector('[data-push-switch]')).toBeNull();
    }
  });
});

describe('the device: what it can do', () => {
  const win = (ua: string, extra: Record<string, unknown> = {}, standalone = false) => ({
    navigator: { userAgent: ua, platform: 'Linux', maxTouchPoints: 0, serviceWorker: {}, ...extra },
    matchMedia: () => ({ matches: standalone }),
    PushManager: class {}, Notification: { permission: 'default' },
  }) as unknown as Window & typeof globalThis;

  it('needs the Home Screen on an iPhone or iPad until opened from there', () => {
    expect(createPusher(win('Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X)')).support()).toBe('needs-home-screen');
    expect(createPusher(win('Mozilla/5.0 (Macintosh)', { platform: 'MacIntel', maxTouchPoints: 5 })).support()).toBe('needs-home-screen');
    expect(createPusher(win('Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X)', {}, true)).support()).toBe('ok');
  });

  it('subscribes with the server key, dropping an old subscription made with another key, and keeping one made with this key', async () => {
    const key = 'AQID';   // the bytes 1, 2, 3
    const made = { toJSON: () => SUB };
    const run = async (oldKey: number[] | null) => {
      const old = oldKey === null ? null : { options: { applicationServerKey: new Uint8Array(oldKey).buffer }, unsubscribe: vi.fn(async () => true) };
      const subscribe = vi.fn(async () => made);
      const w = {
        ...win('Mozilla/5.0 (Linux; Android 15)', { serviceWorker: { ready: Promise.resolve({ pushManager: { getSubscription: async () => old, subscribe } }) } }),
        Notification: { permission: 'default', requestPermission: async () => 'granted' },
      } as unknown as Window & typeof globalThis;
      expect(await createPusher(w).subscribe(key)).toEqual(SUB);
      expect([...(subscribe.mock.calls[0] as unknown as [{ applicationServerKey: Uint8Array }])[0].applicationServerKey]).toEqual([1, 2, 3]);
      return old?.unsubscribe.mock.calls.length ?? 0;
    };
    expect(await run([9, 9, 9])).toBe(1);
    expect(await run([1, 2])).toBe(1);
    expect(await run([1, 2, 3])).toBe(0);
    expect(await run(null)).toBe(0);
  });

  it('is ok on Android, and cannot without push support', () => {
    expect(createPusher(win('Mozilla/5.0 (Linux; Android 15)')).support()).toBe('ok');
    const bare = { ...win('Mozilla/5.0 (Linux; Android 15)'), PushManager: undefined } as Record<string, unknown>;
    delete bare['PushManager'];
    expect(createPusher(bare as unknown as Window & typeof globalThis).support()).toBe('unsupported');
  });
});

/** The service worker, run in a sandbox of its own with a pretend `self`. */
function worker(windows: Array<{ url: string; focus: () => Promise<unknown>; navigate?: (u: string) => Promise<unknown> }> = []) {
  const listeners: Record<string, (e: unknown) => void> = {};
  const shown: Array<{ title: string; options: Record<string, unknown> }> = [];
  const openWindow = vi.fn(async () => null);
  const self = {
    location: { origin: 'https://gom.test' },
    addEventListener: (type: string, fn: (e: unknown) => void) => { listeners[type] = fn; },
    registration: { showNotification: async (title: string, options: Record<string, unknown>) => { shown.push({ title, options }); } },
    clients: { matchAll: async () => windows, openWindow, claim: async () => undefined },
  };
  new Function('self', 'caches', swSource)(self, {});
  const fire = async (type: string, event: Record<string, unknown>) => {
    let done: Promise<unknown> = Promise.resolve();
    listeners[type]!({ ...event, waitUntil: (p: Promise<unknown>) => { done = p; } });
    await done;
  };
  return { fire, shown, openWindow };
}

describe('service worker: notifications', () => {
  const C = '3f2b8c1e-0000-4000-8000-000000000001';

  it('shows what the server wrote, one per conversation, opening that conversation', async () => {
    const w = worker();
    await w.fire('push', { data: { json: () => ({ c: C, title: 'Nhà mình', body: 'Minh: Đã gửi 2 ảnh' }) } });
    expect(w.shown).toEqual([{ title: 'Nhà mình', options: expect.objectContaining({ body: 'Minh: Đã gửi 2 ảnh', tag: C, renotify: true, data: { url: `/c/${C}` } }) }]);
  });

  it('still shows something for a push it cannot read, and never opens an address it was sent', async () => {
    const w = worker();
    await w.fire('push', { data: { json: () => { throw new SyntaxError('bad'); } } });
    await w.fire('push', { data: null });
    await w.fire('push', { data: { json: () => ({ c: '//evil.test', title: 5 }) } });
    expect(w.shown.map((s) => [s.title, s.options['body'], (s.options['data'] as { url: string }).url]))
      .toEqual([['Gom', 'Tin nhắn mới', '/'], ['Gom', 'Tin nhắn mới', '/'], ['Gom', 'Tin nhắn mới', '/']]);
  });

  it('a tap brings an open Gom window forward on the conversation, or opens one', async () => {
    const navigate = vi.fn(async (u: string) => ({ url: u }));
    const win = { url: 'https://gom.test/', focus: vi.fn(async () => win), navigate };
    const open = worker([win]);
    const close = vi.fn();
    await open.fire('notificationclick', { notification: { close, data: { url: `/c/${C}` } } });
    expect(close).toHaveBeenCalled();
    expect(win.focus).toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(`https://gom.test/c/${C}`);
    expect(open.openWindow).not.toHaveBeenCalled();

    const none = worker([]);
    await none.fire('notificationclick', { notification: { close: vi.fn(), data: { url: `/c/${C}` } } });
    expect(none.openWindow).toHaveBeenCalledWith(`https://gom.test/c/${C}`);
  });
});
