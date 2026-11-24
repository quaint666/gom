import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createApi, type GroupMember } from '../Scripts/api.js';
import { mountGroup } from '../Scripts/pages/group.js';
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
    const calls = fetchFn.mock.calls.map((c) => [(c as unknown as [string, RequestInit])[0], (c as unknown as [string, RequestInit])[1].method]);
    expect(calls).toEqual([
      ['/api/conversations/group', 'POST'], [`/api/conversations/${G}/members`, 'GET'], [`/api/conversations/${G}/members`, 'POST'],
      [`/api/conversations/${G}/members/me`, 'DELETE'], [`/api/conversations/${G}/members/u-1/friend-request`, 'POST'],
    ]);
    expect(JSON.parse((fetchFn.mock.calls[0] as unknown as [string, RequestInit])[1].body as string)).toEqual({ title: 'Nhà', members: ['a', 'b'] });
  });

  it('knows the new error codes and says each in words', async () => {
    for (const code of ['invalid_title', 'no_members', 'group_too_big', 'already_member', 'group_full', 'not_a_group'] as const) {
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

describe('group page', () => {
  const members: GroupMember[] = [
    { id: 'u-mom', displayName: 'Mẹ', isMe: true, isFriend: false, gomId: 'mom', request: 'none' },
    { id: 'u-minh', displayName: 'Minh', isMe: false, isFriend: true, gomId: 'minh', request: 'none' },
    { id: 'u-ba', displayName: 'Chị Ba', isMe: false, isFriend: false, gomId: null, request: 'none' },
    { id: 'u-tu', displayName: 'Chú Tư', isMe: false, isFriend: false, gomId: null, request: 'sent' },
    { id: 'u-nam', displayName: 'Anh Nam', isMe: false, isFriend: false, gomId: null, request: 'received' },
  ];
  const friends = { friends: [{ gomId: 'minh', displayName: 'Minh' }, { gomId: 'lan', displayName: 'Lan' }], incoming: [], outgoing: [] };
  const api = (extra = {}) => fakeApi({ me: vi.fn(async () => ok(me)), groupMembers: vi.fn(async () => ok(members)), friends: vi.fn(async () => ok(friends)), ...extra });
  const row = (id: string) => root.querySelector(`[data-member="${id}"]`)!;

  it('sends a signed-out visitor to sign in', async () => {
    const go = vi.fn();
    mountGroup(root, fakeApi(), go, G);
    await flush();
    expect(go).toHaveBeenCalledWith('/sign-in');
  });

  it('lists everyone, shows a Gom ID only where there is one, and marks you', async () => {
    mountGroup(root, api(), vi.fn(), G);
    await flush();
    expect(text(row('u-mom'))).toContain('Bạn');
    expect(text(row('u-minh'))).toContain('@minh');
    expect(text(row('u-ba'))).toContain('Chị Ba');
    expect(text(row('u-ba'))).not.toContain('@');
    expect(root.querySelector('.av')!.textContent).toBeTruthy();
  });

  it('offers "Kết bạn" to members who are not friends, and not to friends or to you', async () => {
    mountGroup(root, api(), vi.fn(), G);
    await flush();
    expect(row('u-ba').querySelector('button[data-befriend]')).not.toBeNull();
    expect(row('u-minh').querySelector('button')).toBeNull();
    expect(row('u-mom').querySelector('button')).toBeNull();
  });

  it('asks by the member\'s opaque id, says so, and reloads', async () => {
    const a = api({ askMember: vi.fn(async () => ok({ status: 'pending' })) });
    mountGroup(root, a, vi.fn(), G);
    await flush();
    row('u-ba').querySelector<HTMLButtonElement>('button[data-befriend]')!.click();
    await flush();
    expect(a.askMember).toHaveBeenCalledWith(G, 'u-ba');
    expect(a.groupMembers).toHaveBeenCalledTimes(2);
  });

  it('shows a request already sent as waiting, and one received as something to accept', async () => {
    const a = api({ askMember: vi.fn(async () => ok({ status: 'friends' })) });
    mountGroup(root, a, vi.fn(), G);
    await flush();
    expect(text(row('u-tu'))).toContain('Đã gửi lời mời');
    expect(row('u-tu').querySelector('button')).toBeNull();
    const accept = row('u-nam').querySelector<HTMLButtonElement>('button[data-befriend]')!;
    expect(accept.textContent).toContain('Chấp nhận');
    accept.click();
    await flush();
    expect(a.askMember).toHaveBeenCalledWith(G, 'u-nam');   // asking back is how a received request is accepted
  });

  it('offers only your friends who are not in the group, and adds one', async () => {
    const a = api();
    mountGroup(root, a, vi.fn(), G);
    await flush();
    const choices = [...root.querySelectorAll<HTMLElement>('[data-addable]')].map((e) => e.getAttribute('data-addable'));
    expect(choices).toEqual(['lan']);                       // Minh is already in
    root.querySelector<HTMLButtonElement>('[data-addable="lan"] button')!.click();
    await flush();
    expect(a.addMember).toHaveBeenCalledWith(G, 'lan');
    expect(a.groupMembers).toHaveBeenCalledTimes(2);
  });

  it('asks twice before leaving, then goes to the list', async () => {
    const go = vi.fn();
    const a = api();
    mountGroup(root, a, go, G);
    await flush();

    root.querySelector<HTMLButtonElement>('button[data-leave]')!.click();
    await flush();
    expect(a.leaveGroup).not.toHaveBeenCalled();
    expect(text(root)).toContain('Rời nhóm này?');

    root.querySelector<HTMLButtonElement>('button[data-confirm-leave]')!.click();
    await flush();
    expect(a.leaveGroup).toHaveBeenCalledWith(G);
    expect(go).toHaveBeenCalledWith('/');
  });

  it('can change its mind about leaving', async () => {
    const a = api();
    mountGroup(root, a, vi.fn(), G);
    await flush();
    root.querySelector<HTMLButtonElement>('button[data-leave]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('button[data-cancel-leave]')!.click();
    await flush();
    expect(root.querySelector('button[data-confirm-leave]')).toBeNull();
    expect(root.querySelector('button[data-leave]')).not.toBeNull();
  });

  it('has a way back to the conversation', async () => {
    mountGroup(root, api(), vi.fn(), G);
    await flush();
    expect(root.querySelector(`header a[href="/c/${G}"]`)).not.toBeNull();
  });

  it('shows a failure in words, such as asking about something that is not a group', async () => {
    mountGroup(root, api({ groupMembers: vi.fn(async () => fail('unknown', 404)) }), vi.fn(), G);
    await flush();
    expect(root.querySelector('[role="alert"]')).not.toBeNull();
  });
});
