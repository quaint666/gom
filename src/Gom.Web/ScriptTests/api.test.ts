import { describe, expect, it, vi } from 'vitest';
import { createApi, REQUEST_TIMEOUT_MS, UPLOAD_STALL_MS } from '../Scripts/api.js';
import { errorText } from '../Scripts/text.js';
import textSource from '../Scripts/text.ts?raw';

const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });

describe('api', () => {
  it('signs in with a JSON POST to /api/session, same-origin, and returns the user', async () => {
    const fetchFn = vi.fn(async () => json({ gomId: 'me', displayName: 'Me', isOwner: false }));
    const result = await createApi(fetchFn).signIn('me', 'secret password');

    const [url, init] = fetchFn.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/session');
    expect(init.method).toBe('POST');
    expect(init.credentials).toBe('same-origin');
    expect(new Headers(init.headers).get('Content-Type')).toBe('application/json');
    expect(JSON.parse(init.body as string)).toEqual({ gomId: 'me', password: 'secret password' });
    expect(result).toEqual({ ok: true, data: { gomId: 'me', displayName: 'Me', isOwner: false } });
  });

  it('says which page code it runs with every request, and tells the page when the server wants a newer one', async () => {
    const fetchFn = vi.fn(async () => json({ error: 'update_required' }, 409));
    const onTooOld = vi.fn();
    const api = createApi(fetchFn, undefined, { onTooOld });
    await api.me();
    await api.signIn('a', 'b');
    for (const call of fetchFn.mock.calls as unknown as [string, RequestInit][]) expect(new Headers(call[1].headers).get('Gom-Page-Version')).toBe('1');
    expect(onTooOld).toHaveBeenCalledTimes(2);
    expect(errorText({ ok: false, status: 409, error: 'update_required' })).toBe('Gom vừa được cập nhật. Tải lại trang để dùng tiếp.');
  });

  it('turns a 401 with an error code into that code', async () => {
    const api = createApi(async () => json({ error: 'invalid_credentials' }, 401));
    expect(await api.signIn('a', 'b')).toEqual({ ok: false, status: 401, error: 'invalid_credentials' });
  });

  it('reads Retry-After on a 429', async () => {
    const api = createApi(async () => json({ error: 'too_many_attempts' }, 429, { 'Retry-After': '7' }));
    expect(await api.signIn('a', 'b')).toEqual({ ok: false, status: 429, error: 'too_many_attempts', retryAfter: 7 });
  });

  it('lets every code that has its own words through as itself, so the page never says only "something went wrong"', async () => {
    // Every case of errorText except the page's own codes and the default.
    const own = new Set(['unauthorized', 'forbidden', 'network', 'server', 'unknown']);
    const worded = [...textSource.matchAll(/case '([a-z_]+)'/g)].map((m) => m[1]!).filter((c) => !own.has(c));
    expect(worded).toContain('invalid_endpoint');
    for (const code of worded) {
      const failure = await createApi(async () => json({ error: code }, 400)).me();
      expect(failure, code).toMatchObject({ ok: false, error: code });
      if (!failure.ok) expect(errorText(failure), code).not.toBe(errorText({ ok: false, status: 400, error: 'unknown' }));
    }
  });

  it('gives every request a time limit, a longer one for the owner\'s trims, and calls a request that ran out "network"', async () => {
    const limits: number[] = [];
    const real = AbortSignal.timeout.bind(AbortSignal);
    const spy = vi.spyOn(AbortSignal, 'timeout').mockImplementation((ms: number) => { limits.push(ms); return real(ms); });
    try {
      const seen: (AbortSignal | null | undefined)[] = [];
      const api = createApi(async (_url, init) => { seen.push(init?.signal); return json({}); });
      await api.me();
      await api.trimMessages({ before: '2026-01-01T00:00:00Z', where: 'all' }, 'Delete');
      await api.trimFiles({ before: '2026-01-01T00:00:00Z', kind: 'all', where: 'all', minBytes: null }, 'Delete');
      expect(limits).toEqual([REQUEST_TIMEOUT_MS, 300_000, 300_000]);
      expect(seen.every((s) => s instanceof AbortSignal)).toBe(true);
    } finally { spy.mockRestore(); }
    const late = createApi(async () => { throw new DOMException('The operation timed out.', 'TimeoutError'); });
    expect(await late.me()).toEqual({ ok: false, status: 0, error: 'network' });
  });

  it('calls a 401 without a body "unauthorized" and a 403 "forbidden"', async () => {
    expect(await createApi(async () => new Response(null, { status: 401 })).me()).toMatchObject({ ok: false, error: 'unauthorized' });
    expect(await createApi(async () => new Response(null, { status: 403 })).me()).toMatchObject({ ok: false, error: 'forbidden' });
  });

  it('calls a server error "server" and a dropped connection "network"', async () => {
    expect(await createApi(async () => new Response('<html>', { status: 502 })).me()).toMatchObject({ ok: false, error: 'server' });
    expect(await createApi(async () => { throw new TypeError('Failed to fetch'); }).me()).toMatchObject({ ok: false, error: 'network' });
  });

  it('treats 204 as success without a body', async () => {
    expect(await createApi(async () => new Response(null, { status: 204 })).signOut()).toEqual({ ok: true, data: undefined });
  });

  it('puts the token in the path, encoded', async () => {
    const fetchFn = vi.fn(async () => json({ invitedBy: 'x' }));
    await createApi(fetchFn).peekInvite('a/b c');
    expect((fetchFn.mock.calls[0] as unknown as [string])[0]).toBe('/api/join/a%2Fb%20c');
  });

  it('uses the right verb and path for each call', async () => {
    const fetchFn = vi.fn(async () => json([]));
    const api = createApi(fetchFn);
    await api.sessions(); await api.endSession('s1'); await api.createInvite(); await api.createInvite({ days: 30, uses: 0 }); await api.invites();
    await api.revokeInvite('i1'); await api.members(); await api.resetLink('mom'); await api.signOut();
    const bodies = fetchFn.mock.calls.map((c) => (c as unknown as [string, RequestInit])[1].body);
    expect(bodies[2]).toBeUndefined();
    expect(JSON.parse(bodies[3] as string)).toEqual({ days: 30, uses: 0 });
    const calls = fetchFn.mock.calls.map((c) => { const [url, init] = c as unknown as [string, RequestInit]; return `${init.method ?? 'GET'} ${url}`; });
    expect(calls).toEqual([
      'GET /api/sessions', 'DELETE /api/sessions/s1', 'POST /api/invites', 'POST /api/invites', 'GET /api/invites',
      'DELETE /api/invites/i1', 'GET /api/users', 'POST /api/users/mom/reset-link', 'DELETE /api/session',
    ]);
  });
});

describe('api: uploading a picture or a file', () => {
  class FakeXhr {
    method = ''; url = ''; headers: Record<string, string> = {}; withCredentials = false; sent: unknown = null;
    status = 0; responseText = '';
    upload: { onprogress: ((e: { lengthComputable: boolean; loaded: number; total: number }) => void) | null } = { onprogress: null };
    onload: (() => void) | null = null; onerror: (() => void) | null = null; ontimeout: (() => void) | null = null; onabort: (() => void) | null = null;
    aborted = false;
    abort() { this.aborted = true; this.onabort?.(); }
    open(method: string, url: string) { this.method = method; this.url = url; }
    setRequestHeader(k: string, v: string) { this.headers[k] = v; }
    send(body: unknown) { this.sent = body; }
  }
  const make = () => { const x = new FakeXhr(); return { x, api: createApi(vi.fn(), () => x as unknown as XMLHttpRequest) }; };
  const file = Object.assign(new Blob([new Uint8Array([1, 2, 3])]), { name: 'ảnh nghỉ hè.jpg' });

  it('posts the raw bytes with the name in the query, reports progress, and gives back the attachment', async () => {
    const { x, api } = make();
    const seen: number[] = [];
    const pending = api.upload('c1', file, (f) => seen.push(f));
    x.upload.onprogress!({ lengthComputable: true, loaded: 1, total: 4 });
    x.upload.onprogress!({ lengthComputable: false, loaded: 2, total: 0 });
    x.status = 201; x.responseText = JSON.stringify({ id: 'a1', kind: 'image', name: 'x.webp', size: 3, width: 1, height: 1 });
    x.onload!();
    const result = await pending;
    expect(x.method).toBe('POST');
    expect(x.url).toBe(`/api/conversations/c1/attachments?name=${encodeURIComponent('ảnh nghỉ hè.jpg')}`);
    expect(x.withCredentials).toBe(true);
    expect(x.headers['Content-Type']).toBe('application/octet-stream');
    expect(x.headers['Gom-Page-Version']).toBe('1');
    expect(x.sent).toBe(file);
    expect(seen).toEqual([0.25]);
    expect(result).toEqual({ ok: true, data: { id: 'a1', kind: 'image', name: 'x.webp', size: 3, width: 1, height: 1 } });
  });

  it('turns the server\'s reason, a proxy\'s plain 413, and a lost connection into an error', async () => {
    let r = make();
    let pending = r.api.upload('c1', file, () => undefined);
    r.x.status = 422; r.x.responseText = '{"error":"invalid_image"}'; r.x.onload!();
    expect(await pending).toMatchObject({ ok: false, status: 422, error: 'invalid_image' });

    r = make(); pending = r.api.upload('c1', file, () => undefined);
    r.x.status = 413; r.x.responseText = '<html>too big</html>'; r.x.onload!();
    expect(await pending).toMatchObject({ ok: false, status: 413, error: 'too_large' });

    r = make(); pending = r.api.upload('c1', file, () => undefined);
    r.x.status = 502; r.x.responseText = ''; r.x.onload!();
    expect(await pending).toMatchObject({ ok: false, error: 'server' });

    r = make(); pending = r.api.upload('c1', file, () => undefined);
    r.x.status = 401; r.x.responseText = '{}'; r.x.onload!();
    expect(await pending).toMatchObject({ ok: false, error: 'unauthorized' });

    r = make(); pending = r.api.upload('c1', file, () => undefined);
    r.x.onerror!();
    expect(await pending).toEqual({ ok: false, status: 0, error: 'network' });
  });

  it('gives up on an upload when nothing has moved for a minute, but not while it keeps moving', async () => {
    vi.useFakeTimers();
    try {
      const { x, api } = make();
      const pending = api.upload('c1', file, () => undefined);
      vi.advanceTimersByTime(UPLOAD_STALL_MS - 1000);
      x.upload.onprogress!({ lengthComputable: true, loaded: 1, total: 4 });   // moving: the wait starts again
      vi.advanceTimersByTime(UPLOAD_STALL_MS - 1000);
      expect(x.aborted).toBe(false);
      vi.advanceTimersByTime(2000);
      expect(x.aborted).toBe(true);
      expect(await pending).toEqual({ ok: false, status: 0, error: 'network' });
    } finally { vi.useRealTimers(); }
  });

  it('gives up on an upload that never moves at all', async () => {
    vi.useFakeTimers();
    try {
      const { x, api } = make();
      const pending = api.upload('c1', file, () => undefined);
      vi.advanceTimersByTime(UPLOAD_STALL_MS + 1);
      expect(x.aborted).toBe(true);
      expect(await pending).toMatchObject({ error: 'network' });
    } finally { vi.useRealTimers(); }
  });

  it('an upload that ends in time leaves no timer to abort it later', async () => {
    vi.useFakeTimers();
    try {
      const { x, api } = make();
      const pending = api.upload('c1', file, () => undefined);
      x.status = 201; x.responseText = JSON.stringify({ id: 'a1', kind: 'file', name: 'x', size: 3, width: null, height: null }); x.onload!();
      await pending;
      vi.advanceTimersByTime(UPLOAD_STALL_MS * 2);
      expect(x.aborted).toBe(false);
    } finally { vi.useRealTimers(); }
  });

  it('sends the upload ids with a message only when there are some', async () => {
    const fetchFn = vi.fn(async () => new Response('{}', { status: 200 }));
    const api = createApi(fetchFn);
    await api.send('c1', 'k1', 'hi', undefined, ['a', 'b']);
    await api.send('c1', 'k2', 'hi');
    const bodies = fetchFn.mock.calls.map((c) => JSON.parse((c as unknown as [string, RequestInit])[1].body as string));
    expect(bodies[0]).toMatchObject({ attachments: ['a', 'b'] });
    expect('attachments' in bodies[1]).toBe(false);
  });
});

