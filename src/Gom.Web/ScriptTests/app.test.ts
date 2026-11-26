import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createApi } from '../Scripts/api.js';
import { APP_PROTOCOL, appPort, createAppPusher, type AppPort } from '../Scripts/app.js';
import { mountSettings } from '../Scripts/pages/settings.js';
import { fakeApi, flush, ok } from './helpers.js';

/** The app's object as the page sees it: what the page posted, and a way for the "app" to answer. */
function fakePort() {
  const sent: Array<Record<string, unknown>> = [];
  const port: AppPort = { postMessage: (m: string) => { sent.push(JSON.parse(m) as Record<string, unknown>); }, onmessage: null };
  const answer = (m: unknown) => port.onmessage?.({ data: typeof m === 'string' ? m : JSON.stringify(m) });
  return { port, sent, answer, of: (t: string) => sent.filter((m) => m['t'] === t) };
}

const hello = (over: Record<string, unknown> = {}) => ({ t: 'hello', v: 1, app: '1.0.0', push: true, permission: 'default', ...over });

beforeEach(() => {
  document.head.innerHTML = '<meta name="theme-color" content="#3F6B57">';
  document.body.replaceChildren();
});

describe('app: finding the app', () => {
  it('sees the app only when it put its object on the page', () => {
    expect(appPort(window)).toBeUndefined();
    const w = { gomApp: { postMessage: () => undefined, onmessage: null } } as unknown as Window;
    expect(appPort(w)).toBe((w as unknown as { gomApp: unknown }).gomApp);
    expect(appPort({ gomApp: 'not it' } as unknown as Window)).toBeUndefined();
  });
});

describe('app: notifications through the app', () => {
  it('says hello with its protocol version, and believes what the app says of itself', () => {
    const f = fakePort();
    const pusher = createAppPusher(f.port);
    expect(f.of('hello')).toEqual([{ t: 'hello', v: APP_PROTOCOL, images: 0 }]);
    expect(pusher.support()).toBe('ok');
    f.answer(hello({ permission: 'denied' }));
    expect(pusher.permission()).toBe('denied');
    f.answer(hello({ push: false }));
    expect(pusher.support()).toBe('unsupported');
  });

  it('turning on asks the app, and the subscription is its installation id', async () => {
    const f = fakePort();
    const pusher = createAppPusher(f.port);
    f.answer(hello());
    const asked = pusher.subscribe('ignored');
    expect(f.of('enable')).toHaveLength(1);
    f.answer('not json');   // a stray message is not the answer
    f.answer({ t: 'enabled', fid: 'fid-1' });
    expect(await asked).toEqual({ app: 'fid-1' });
    expect(pusher.permission()).toBe('granted');
    expect(await pusher.current()).toEqual({ app: 'fid-1' });
  });

  it('a refusal or a failure in the app is said as the browser says it', async () => {
    const f = fakePort();
    const pusher = createAppPusher(f.port);
    f.answer(hello());
    const refused = pusher.subscribe('');
    f.answer({ t: 'denied' });
    expect(await refused).toBe('denied');
    expect(pusher.permission()).toBe('denied');
    const failed = pusher.subscribe('');
    f.answer({ t: 'failed' });
    expect(await failed).toBe('failed');
    const empty = pusher.subscribe('');
    f.answer({ t: 'enabled', fid: '' });   // no id is no registration
    f.answer({ t: 'failed' });
    expect(await empty).toBe('failed');
  });

  it('turning off tells the app and forgets the id; opening a chat closes its notifications', async () => {
    const f = fakePort();
    const pusher = createAppPusher(f.port);
    f.answer(hello({ fid: 'fid-1' }));
    await pusher.unsubscribe();
    expect(f.of('disable')).toHaveLength(1);
    expect(await pusher.current()).toBeNull();
    pusher.clear('c-1');
    expect(f.of('clear')).toEqual([{ t: 'clear', c: 'c-1' }]);
  });

  it('hears of its registration from the hello and of each new id FCM gives it', () => {
    const f = fakePort();
    const pusher = createAppPusher(f.port);
    const seen = vi.fn();
    pusher.onRegistration(seen);
    f.answer(hello());
    expect(seen).not.toHaveBeenCalled();   // not registered yet
    f.answer(hello({ fid: 'fid-1' }));
    f.answer({ t: 'fid', fid: 'fid-2' });
    expect(seen.mock.calls).toEqual([[{ app: 'fid-1' }], [{ app: 'fid-2' }]]);
    const late = vi.fn();
    pusher.onRegistration(late);
    expect(late).toHaveBeenCalledWith({ app: 'fid-2' });
  });

  it('tells the app the colours behind the system bars, and again when they change', async () => {
    const f = fakePort();
    createAppPusher(f.port);
    expect(f.of('theme')).toEqual([{ t: 'theme', bar: '#3F6B57', page: expect.any(String) }]);
    document.querySelector('meta[name="theme-color"]')!.setAttribute('content', '#123456');
    await flush();
    expect(f.of('theme').at(-1)).toMatchObject({ bar: '#123456' });
    expect(f.of('theme')).toHaveLength(2);
  });
});

describe('app: pictures from the keyboard', () => {
  it('tells the app the largest picture the server takes', () => {
    const f = fakePort();
    createAppPusher(f.port, window, 5_000_000);
    expect(f.of('hello')[0]).toMatchObject({ images: 5_000_000 });
  });

  it('turns a GIF the keyboard gave into a file, as a paste would, and drops anything else', async () => {
    const f = fakePort();
    const pusher = createAppPusher(f.port);
    const got: File[] = [];
    pusher.onKeyboardImage((file) => got.push(file));
    f.answer({ t: 'image', type: 'image/gif', data: btoa('GIF89a') });
    f.answer({ t: 'image', type: 'text/html', data: btoa('<p>') });
    f.answer({ t: 'image', type: 'image/png', data: '%%% not base64' });
    f.answer({ t: 'image', type: 'image/png', data: '' });
    expect(got.map((g) => [g.name, g.type])).toEqual([['keyboard.gif', 'image/gif']]);
    expect(new TextDecoder().decode(await got[0]!.arrayBuffer())).toBe('GIF89a');
  });
});

describe('app: the server side of its registration', () => {
  it('registers and forgets the app at its own address, with the id as the token', async () => {
    const fetchFn = vi.fn(async () => new Response(null, { status: 204 }));
    const api = createApi(fetchFn as unknown as typeof fetch);
    await api.setPush({ app: 'fid-1' }, 'name');
    await api.removePush({ app: 'fid-1' });
    await api.removePush(null);
    const calls = fetchFn.mock.calls.map((c) => { const [url, init] = c as unknown as [string, RequestInit]; return [url, init.method, init.body ? JSON.parse(init.body as string) : undefined]; });
    expect(calls).toEqual([['/api/push/fcm', 'PUT', { token: 'fid-1', level: 'name' }], ['/api/push/fcm', 'DELETE', undefined], ['/api/push', 'DELETE', undefined]]);
  });

  it('turning notifications off in Settings inside the app forgets the app, not a browser', async () => {
    const root = document.createElement('div');
    document.body.replaceChildren(root);
    const f = fakePort();
    const pusher = createAppPusher(f.port);
    f.answer(hello({ fid: 'fid-1', permission: 'granted' }));
    const api = fakeApi({
      me: vi.fn(async () => ok({ gomId: 'mom', displayName: 'Mẹ', isOwner: false })), sessions: vi.fn(async () => ok([])),
      pushState: vi.fn(async () => ok({ key: '', subscribed: true, level: 'words' as const })),
    });
    history.replaceState(null, '', '/settings/notifications');
    mountSettings(root, api, vi.fn(), undefined, pusher, { path: () => '/settings/notifications', push: vi.fn(), replace: vi.fn(), onPop: vi.fn() });
    await flush();
    const toggle = root.querySelector<HTMLInputElement>('[data-push-switch]')!;
    toggle.checked = false;
    toggle.dispatchEvent(new Event('change'));
    await flush();
    expect(api.removePush).toHaveBeenCalledWith({ app: 'fid-1' });
    expect(f.of('disable')).toHaveLength(1);
  });
});
