import { describe, expect, it, vi } from 'vitest';
import { createApi } from '../Scripts/api.js';

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

  it('turns a 401 with an error code into that code', async () => {
    const api = createApi(async () => json({ error: 'invalid_credentials' }, 401));
    expect(await api.signIn('a', 'b')).toEqual({ ok: false, status: 401, error: 'invalid_credentials' });
  });

  it('reads Retry-After on a 429', async () => {
    const api = createApi(async () => json({ error: 'too_many_attempts' }, 429, { 'Retry-After': '7' }));
    expect(await api.signIn('a', 'b')).toEqual({ ok: false, status: 429, error: 'too_many_attempts', retryAfter: 7 });
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
    await api.sessions(); await api.endSession('s1'); await api.createInvite(); await api.invites();
    await api.revokeInvite('i1'); await api.members(); await api.resetLink('mom'); await api.signOut();
    const calls = fetchFn.mock.calls.map((c) => { const [url, init] = c as unknown as [string, RequestInit]; return `${init.method ?? 'GET'} ${url}`; });
    expect(calls).toEqual([
      'GET /api/sessions', 'DELETE /api/sessions/s1', 'POST /api/invites', 'GET /api/invites',
      'DELETE /api/invites/i1', 'GET /api/users', 'POST /api/users/mom/reset-link', 'DELETE /api/session',
    ]);
  });
});
