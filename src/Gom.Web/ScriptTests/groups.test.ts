import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createApi } from '../Scripts/api.js';
import { mountNewGroup } from '../Scripts/pages/newgroup.js';
import { errorText } from '../Scripts/text.js';
import { fail, fakeApi, fill, flush, ok, submit, text } from './helpers.js';

let root: HTMLElement;
beforeEach(() => { root = document.createElement('div'); document.body.replaceChildren(root); });

const me = { id: 'u-mom', gomId: 'mom', displayName: 'Mẹ', isOwner: false };
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const G = '33333333-3333-4333-8333-333333333333';

describe('api: groups', () => {
  it('uses the right method and path for each call', async () => {
    const fetchFn = vi.fn(async () => new Response(null, { status: 204 }));
    const api = createApi(fetchFn);
    await api.createGroup('Nhà', ['a', 'b']); await api.groupMembers(G); await api.addMember(G, 'x'); await api.leaveGroup(G); await api.askMember(G, 'u-1');
    await api.removeMember(G, 'u-2'); await api.setRole(G, 'u-2', 'deputy'); await api.handOver(G, 'u-2'); await api.disband(G);
    const calls = fetchFn.mock.calls.map((c) => [(c as unknown as [string, RequestInit])[0], (c as unknown as [string, RequestInit])[1].method]);
    expect(calls).toEqual([
      ['/api/conversations/group', 'POST'], [`/api/conversations/${G}/members`, 'GET'], [`/api/conversations/${G}/members`, 'POST'],
      [`/api/conversations/${G}/members/me`, 'DELETE'], [`/api/conversations/${G}/members/u-1/friend-request`, 'POST'],
      [`/api/conversations/${G}/members/u-2`, 'DELETE'], [`/api/conversations/${G}/members/u-2/role`, 'PUT'],
      [`/api/conversations/${G}/leader`, 'POST'], [`/api/conversations/${G}`, 'DELETE'],
    ]);
    expect(JSON.parse((fetchFn.mock.calls[0] as unknown as [string, RequestInit])[1].body as string)).toEqual({ title: 'Nhà', members: ['a', 'b'] });
  });

  it('knows the new error codes and says each in words', async () => {
    for (const code of ['invalid_title', 'no_members', 'group_too_big', 'already_member', 'group_full', 'not_a_group', 'not_allowed', 'leader_must_hand_over', 'invalid_role', 'invalid_member', 'use_leave'] as const) {
      expect(await createApi(async () => json({ error: code }, 400)).createGroup('x', ['y'])).toMatchObject({ ok: false, error: code });
      expect(errorText(fail(code)).length).toBeGreaterThan(10);
      expect(errorText(fail(code))).not.toBe(errorText(fail('unknown')));
    }
  });
});

describe('new group page', () => {
  const friends = { friends: [{ gomId: 'minh', displayName: 'Minh' }, { gomId: 'lan', displayName: 'Lan' }], incoming: [], outgoing: [] };
  const api = (extra = {}) => fakeApi({ me: vi.fn(async () => ok(me)), friends: vi.fn(async () => ok(friends)), ...extra });
  const check = (gomId: string) => { const box = root.querySelector<HTMLInputElement>(`input[type="checkbox"][value="${gomId}"]`)!; box.checked = true; box.dispatchEvent(new Event('change', { bubbles: true })); };

  it('sends a signed-out visitor to sign in', async () => {
    const go = vi.fn();
    mountNewGroup(root, fakeApi(), go);
    await flush();
    expect(go).toHaveBeenCalledWith('/sign-in');
  });

  it('starts with a friend ticked when it comes from "make a group with", and ignores someone who is not a friend', async () => {
    mountNewGroup(root, api(), vi.fn(), 'lan');
    await flush();
    const ticked = (host: HTMLElement) => [...host.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')].filter((b) => b.checked).map((b) => b.value);
    expect(ticked(root)).toEqual(['lan']);
    const other = document.createElement('div');
    mountNewGroup(other, api(), vi.fn(), 'stranger');
    await flush();
    expect(ticked(other)).toEqual([]);
    expect(other.querySelector('[data-picked]')).toBeNull();   // nobody counted as picked either
  });

  it('offers your friends as labelled checkboxes and a name field', async () => {
    mountNewGroup(root, api(), vi.fn());
    await flush();
    const boxes = [...root.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')];
    expect(boxes.map((b) => b.value)).toEqual(['minh', 'lan']);
    for (const b of boxes) expect(root.querySelector(`label[for="${b.id}"]`)).not.toBeNull();
    const name = root.querySelector<HTMLInputElement>('input[name="title"]')!;
    expect(root.querySelector(`label[for="${name.id}"]`)).not.toBeNull();
    expect(name.getAttribute('maxlength')).toBe('60');
  });

  it('searches the friends to pick from, and keeps a pick that the search hides', async () => {
    const go = vi.fn();
    const a = api({ createGroup: vi.fn(async () => ok({ id: G })) });
    mountNewGroup(root, a, go);
    await flush();
    check('minh');
    const find = root.querySelector<HTMLInputElement>('input[name="find"]')!;
    find.value = 'LAN'; find.dispatchEvent(new Event('input', { bubbles: true })); await flush();
    expect([...root.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')].map((b) => b.value)).toEqual(['lan']);
    check('lan');
    expect(text(root.querySelector('[data-picked]')!)).toBe('2');
    find.value = 'zzz'; find.dispatchEvent(new Event('input', { bubbles: true })); await flush();
    expect(root.querySelector('[data-no-match]')).not.toBeNull();
    find.value = ''; find.dispatchEvent(new Event('input', { bubbles: true })); await flush();
    fill(root, 'input[name="title"]', 'Nhà');
    submit(root);
    await flush();
    expect(a.createGroup).toHaveBeenCalledWith('Nhà', ['minh', 'lan']);
  });

  it('makes the group from the name and the friends picked, then opens it', async () => {
    const go = vi.fn();
    const a = api({ createGroup: vi.fn(async () => ok({ id: G })) });
    mountNewGroup(root, a, go);
    await flush();

    fill(root, 'input[name="title"]', '  Gia đình  ');
    check('minh'); check('lan');
    submit(root);
    await flush();

    expect(a.createGroup).toHaveBeenCalledWith('Gia đình', ['minh', 'lan']);
    expect(go).toHaveBeenCalledWith(`/c/${G}`);
  });

  it('sends once even if the button is pressed twice while waiting', async () => {
    let finish: (r: unknown) => void = () => undefined;
    const a = api({ createGroup: vi.fn(() => new Promise((resolve) => { finish = resolve; })) });
    mountNewGroup(root, a, vi.fn());
    await flush();
    fill(root, 'input[name="title"]', 'Nhóm'); check('minh');
    submit(root);
    submit(root);
    await flush();
    expect(a.createGroup).toHaveBeenCalledTimes(1);
    finish(ok({ id: G }));
  });

  it('does not send without a name or without anybody picked, and says why', async () => {
    const a = api({ createGroup: vi.fn(async () => ok({ id: G })) });
    mountNewGroup(root, a, vi.fn());
    await flush();

    check('minh'); submit(root);
    await flush();
    expect(text(root.querySelector('[role="alert"]')!)).toContain('Tên nhóm');

    fill(root, 'input[name="title"]', 'Nhóm');
    root.querySelector<HTMLInputElement>('input[value="minh"]')!.checked = false;
    root.querySelector<HTMLInputElement>('input[value="minh"]')!.dispatchEvent(new Event('change', { bubbles: true }));
    submit(root);
    await flush();
    expect(text(root.querySelector('[role="alert"]')!)).toContain('ít nhất một người bạn');
    expect(a.createGroup).not.toHaveBeenCalled();
  });

  it('shows a refusal in words and keeps the choices', async () => {
    mountNewGroup(root, api({ createGroup: vi.fn(async () => fail('not_friends', 403)) }), vi.fn());
    await flush();
    fill(root, 'input[name="title"]', 'Nhóm'); check('lan');
    submit(root);
    await flush();
    expect(root.querySelector('[role="alert"]')).not.toBeNull();
    expect(root.querySelector<HTMLInputElement>('input[value="lan"]')!.checked).toBe(true);
    expect(root.querySelector<HTMLInputElement>('input[name="title"]')!.value).toBe('Nhóm');
  });

  it('says so, with a way forward, when there are no friends yet', async () => {
    mountNewGroup(root, api({ friends: vi.fn(async () => ok({ friends: [], incoming: [], outgoing: [] })) }), vi.fn());
    await flush();
    expect(text(root)).toContain('Bạn chưa có bạn bè');
    expect(root.querySelector('a[href="/friends"]')).not.toBeNull();
    expect(root.querySelector('button[type="submit"]')).toBeNull();
  });
});
