import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mountSettings } from '../Scripts/pages/settings.js';
import { fail, fakeApi, flush, ok, text } from './helpers.js';

let root: HTMLElement;
beforeEach(() => { root = document.createElement('div'); document.body.replaceChildren(root); });
const me = { gomId: 'mom', displayName: 'Mẹ', isOwner: false };

describe('settings page', () => {
  const sessions = [
    { id: 's1', deviceLabel: 'Chrome on Android', createdAt: '2026-12-01T09:00:00Z', lastSeenAt: '2026-12-02T09:00:00Z', current: true },
    { id: 's2', deviceLabel: 'Safari on iPhone', createdAt: '2026-11-20T09:00:00Z', lastSeenAt: '2026-11-25T09:00:00Z', current: false },
  ];

  function api(extra = {}) {
    return fakeApi({ me: vi.fn(async () => ok(me)), sessions: vi.fn(async () => ok(sessions)), ...extra });
  }

  it('lists the devices and marks this one, with an end button only on the others', async () => {
    mountSettings(root, api(), vi.fn());
    await flush();
    const rows = [...root.querySelectorAll('[data-session]')];
    expect(rows).toHaveLength(2);
    expect(text(rows[0]!)).toContain('Chrome on Android');
    expect(text(rows[0]!)).toContain('Thiết bị này');
    expect(rows[0]!.querySelector('button')).toBeNull();
    expect(rows[1]!.querySelector('button')).not.toBeNull();
  });

  it('ends another session and reloads the list', async () => {
    const a = api();
    mountSettings(root, a, vi.fn());
    await flush();
    root.querySelector<HTMLButtonElement>('[data-session="s2"] button')!.click();
    await flush();
    expect(a.endSession).toHaveBeenCalledWith('s2');
    expect(a.sessions).toHaveBeenCalledTimes(2);
  });

  it('signs out and goes to the sign-in page', async () => {
    const a = api();
    const go = vi.fn();
    mountSettings(root, a, go);
    await flush();
    root.querySelector<HTMLButtonElement>('button[data-signout]')!.click();
    await flush();
    expect(a.signOut).toHaveBeenCalled();
    expect(go).toHaveBeenCalledWith('/sign-in');
  });

  it('shows the owner tools link only to the owner', async () => {
    mountSettings(root, api(), vi.fn());
    await flush();
    expect(root.querySelector('a[href="/owner"]')).toBeNull();
    const ownerRoot = document.createElement('div');
    mountSettings(ownerRoot, api({ me: vi.fn(async () => ok({ ...me, isOwner: true })) }), vi.fn());
    await flush();
    expect(ownerRoot.querySelector('a[href="/owner"]')).not.toBeNull();
  });

  it('goes to sign-in when the session is gone', async () => {
    const go = vi.fn();
    mountSettings(root, fakeApi({ me: vi.fn(async () => fail('unauthorized', 401)) }), go);
    await flush();
    expect(go).toHaveBeenCalledWith('/sign-in');
  });
});
