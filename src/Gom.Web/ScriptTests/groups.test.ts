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

describe('group page', () => {
  const members: GroupMember[] = [
    { id: 'u-mom', displayName: 'Mẹ', isMe: true, isFriend: false, gomId: 'mom', request: 'none', role: 'member' },
    { id: 'u-minh', displayName: 'Minh', isMe: false, isFriend: true, gomId: 'minh', request: 'none', role: 'member' },
    { id: 'u-ba', displayName: 'Chị Ba', isMe: false, isFriend: false, gomId: null, request: 'none', role: 'member' },
    { id: 'u-tu', displayName: 'Chú Tư', isMe: false, isFriend: false, gomId: null, request: 'sent', role: 'member' },
    { id: 'u-nam', displayName: 'Anh Nam', isMe: false, isFriend: false, gomId: null, request: 'received', role: 'member' },
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

describe('group page: leader and deputies', () => {
  const person = (id: string, name: string, role: GroupMember['role'], isMe = false): GroupMember =>
    ({ id, displayName: name, isMe, isFriend: false, gomId: null, request: 'sent', role });
  const team = (myRole: GroupMember['role']): GroupMember[] => [
    person('u-me', 'Mẹ', myRole, true), person('u-lead', 'Bố', myRole === 'leader' ? 'member' : 'leader'),
    person('u-dep', 'Cô Hai', 'deputy'), person('u-plain', 'Chú Tư', 'member'),
  ];
  const mount = (myRole: GroupMember['role'], extra = {}, list?: GroupMember[]) => {
    const a = fakeApi({ me: vi.fn(async () => ok(me)), groupMembers: vi.fn(async () => ok(list ?? team(myRole))), friends: vi.fn(async () => ok({ friends: [], incoming: [], outgoing: [] })), ...extra });
    const go = vi.fn();
    mountGroup(root, a, go, G);
    return { a, go };
  };
  const row = (id: string) => root.querySelector(`[data-member="${id}"]`)!;
  const click = (selector: string) => root.querySelector<HTMLButtonElement>(selector)!.click();
  const open = async (id: string) => { row(id).querySelector<HTMLButtonElement>('[data-manage]')!.click(); await flush(); };

  it('shows who leads and who is a deputy', async () => {
    mount('member');
    await flush();
    expect(row('u-lead').querySelector('[data-role="leader"]')!.textContent).toBe('Trưởng nhóm');
    expect(row('u-dep').querySelector('[data-role="deputy"]')!.textContent).toBe('Phó nhóm');
    expect(row('u-plain').querySelector('[data-role]')).toBeNull();
  });

  it('gives a plain member no controls over anyone, and no way to disband', async () => {
    mount('member');
    await flush();
    expect(root.querySelector('[data-manage]')).toBeNull();
    expect(root.querySelector('[data-disband]')).toBeNull();
  });

  it('lets a deputy manage plain members only', async () => {
    mount('deputy');
    await flush();
    expect(row('u-plain').querySelector('[data-manage]')).not.toBeNull();
    expect(row('u-lead').querySelector('[data-manage]')).toBeNull();
    expect(row('u-dep').querySelector('[data-manage]')).toBeNull();
    await open('u-plain');
    expect(root.querySelector('[data-remove]')).not.toBeNull();
    expect(root.querySelector('[data-make-deputy]')).toBeNull();
    expect(root.querySelector('[data-hand-over]')).toBeNull();
    expect(root.querySelector('[data-disband]')).toBeNull();
  });

  it('lets the leader manage everyone else, with the right choices for a member and for a deputy', async () => {
    mount('leader');
    await flush();
    expect(row('u-me').querySelector('[data-manage]')).toBeNull();
    await open('u-plain');
    expect(root.querySelector('[data-make-deputy]')).not.toBeNull();
    expect(root.querySelector('[data-drop-deputy]')).toBeNull();
    await open('u-dep');
    expect(root.querySelector('[data-drop-deputy]')).not.toBeNull();
    expect(root.querySelector('[data-make-deputy]')).toBeNull();
    expect(root.querySelector('[data-disband]')).not.toBeNull();
  });

  it('names and drops a deputy at once, then reloads', async () => {
    const { a } = mount('leader');
    await flush();
    await open('u-plain'); click('[data-make-deputy]'); await flush();
    expect(a.setRole).toHaveBeenCalledWith(G, 'u-plain', 'deputy');
    await open('u-dep'); click('[data-drop-deputy]'); await flush();
    expect(a.setRole).toHaveBeenCalledWith(G, 'u-dep', 'member');
    expect(a.groupMembers).toHaveBeenCalledTimes(3);
  });

  it('asks before removing, says who, and removes only after yes', async () => {
    const { a } = mount('leader');
    await flush();
    await open('u-plain'); click('[data-remove]'); await flush();
    expect(text(root)).toContain('Xóa Chú Tư khỏi nhóm?');
    expect(a.removeMember).not.toHaveBeenCalled();
    click('[data-confirm-sure]'); await flush();
    expect(a.removeMember).toHaveBeenCalledWith(G, 'u-plain', false);
    expect(a.groupMembers).toHaveBeenCalledTimes(2);
  });

  it('drops the question when you say no', async () => {
    const { a } = mount('leader');
    await flush();
    await open('u-plain'); click('[data-remove]'); await flush();
    click('[data-cancel-sure]'); await flush();
    expect(a.removeMember).not.toHaveBeenCalled();
    expect(root.querySelector('[data-confirm-sure]')).toBeNull();
  });

  it('asks before handing over leadership', async () => {
    const { a } = mount('leader');
    await flush();
    await open('u-dep'); click('[data-hand-over]'); await flush();
    expect(text(root)).toContain('Chuyển quyền trưởng nhóm cho Cô Hai?');
    expect(a.handOver).not.toHaveBeenCalled();
    click('[data-confirm-sure]'); await flush();
    expect(a.handOver).toHaveBeenCalledWith(G, 'u-dep');
  });

  it('asks before disbanding and goes home afterwards', async () => {
    const { a, go } = mount('leader');
    await flush();
    click('[data-disband]'); await flush();
    expect(text(root)).toContain('Giải tán nhóm này?');
    // The question takes the button's place, after "Rời nhóm", not before it.
    expect(root.querySelector('[data-disband]')).toBeNull();
    const leave = root.querySelector('[data-leave]')!;
    expect(leave.compareDocumentPosition(root.querySelector('[data-confirm-sure]')!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(a.disband).not.toHaveBeenCalled();
    click('[data-confirm-sure]'); await flush();
    expect(a.disband).toHaveBeenCalledWith(G);
    expect(go).toHaveBeenCalledWith('/');
  });

  it('stays on the page and says why when the server refuses', async () => {
    const { go } = mount('leader', { removeMember: vi.fn(async () => fail('not_allowed')) });
    await flush();
    await open('u-plain'); click('[data-remove]'); await flush(); click('[data-confirm-sure]'); await flush();
    expect(text(root)).toContain('Bạn không có quyền');
    expect(go).not.toHaveBeenCalled();
  });

  it('keeps a leader with others in the group from leaving, and says to hand over first', async () => {
    mount('leader');
    await flush();
    expect(root.querySelector<HTMLButtonElement>('[data-leave]')!.disabled).toBe(true);
    expect(root.querySelector('[data-leader-note]')).not.toBeNull();
  });

  it('lets a leader who is alone leave', async () => {
    mount('leader', {}, [person('u-me', 'Mẹ', 'leader', true)]);
    await flush();
    expect(root.querySelector<HTMLButtonElement>('[data-leave]')!.disabled).toBe(false);
    expect(root.querySelector('[data-leader-note]')).toBeNull();
  });
});

describe('group page: the name', () => {
  const person = (id: string, name: string, role: GroupMember['role'], isMe = false): GroupMember =>
    ({ id, displayName: name, isMe, isFriend: false, gomId: null, request: 'sent', role });
  const chats = (title: string) => [{ id: G, kind: 'group', title, peer: null, lastSeq: 0, lastReadSeq: 0, unread: 0, lastMessage: null, memberCount: 2 }];
  const mount = (myRole: GroupMember['role'], extra = {}) => {
    let title = 'Nhà cũ';
    const a = fakeApi({
      me: vi.fn(async () => ok(me)), groupMembers: vi.fn(async () => ok([person('u-me', 'Mẹ', myRole, true), person('u-x', 'Bố', myRole === 'leader' ? 'member' : 'leader')])),
      friends: vi.fn(async () => ok({ friends: [], incoming: [], outgoing: [] })), conversations: vi.fn(async () => ok(chats(title))),
      renameGroup: vi.fn(async (_c: string, t: string) => { title = t; return ok(undefined); }), ...extra,
    });
    mountGroup(root, a, vi.fn(), G);
    return a;
  };
  const field = () => root.querySelector<HTMLInputElement>('form[data-rename] input')!;
  const save = () => root.querySelector<HTMLButtonElement>('form[data-rename] button[type="submit"]')!;
  const type = (value: string) => { field().value = value; field().dispatchEvent(new Event('input', { bubbles: true })); };

  it('shows the name to everyone and a form to change it to the leader and the deputies only', async () => {
    mount('member');
    await flush();
    expect(text(root.querySelector('[data-group-name]')!)).toBe('Nhà cũ');
    expect(root.querySelector('form[data-rename]')).toBeNull();
    for (const role of ['leader', 'deputy'] as const) {
      mount(role);
      await flush();
      expect(field().value).toBe('Nhà cũ');
      expect(field().parentElement!.classList.contains('input')).toBe(true);   // the site's own box, as on every other form
    }
  });

  it('does not offer to save an empty name or the name it already has', async () => {
    mount('leader');
    await flush();
    expect(save().disabled).toBe(true);
    type('   '); await flush();
    expect(save().disabled).toBe(true);
    type('Nhà mới'); await flush();
    expect(save().disabled).toBe(false);
  });

  it('saves the trimmed name, then shows what the server now says', async () => {
    const a = mount('leader');
    await flush();
    type('  Nhà mới  '); await flush();
    submit(root);
    await flush();
    expect(a.renameGroup).toHaveBeenCalledWith(G, 'Nhà mới');
    expect(field().value).toBe('Nhà mới');
    expect(save().disabled).toBe(true);
  });

  it('keeps what was typed and says why when the server refuses', async () => {
    mount('leader', { renameGroup: vi.fn(async () => fail('not_allowed', 403)) });
    await flush();
    type('Tên mới'); await flush();
    submit(root);
    await flush();
    expect(text(root)).toContain('Bạn không có quyền');
    expect(field().value).toBe('Tên mới');
  });
});

describe('group page: approval, requests and bans', () => {
  const person = (id: string, name: string, role: GroupMember['role'], isMe = false): GroupMember =>
    ({ id, displayName: name, isMe, isFriend: false, gomId: null, request: 'sent', role });
  const rows = (myRole: GroupMember['role']) => [person('u-me', 'Mẹ', myRole, true), person('u-x', 'Bố', myRole === 'leader' ? 'member' : 'leader'), person('u-y', 'Chú Tư', 'member')];
  const chats = (approval: boolean) => [{ id: G, kind: 'group', title: 'Nhà', peer: null, lastSeq: 0, lastReadSeq: 0, unread: 0, lastMessage: null, memberCount: 3, joinApproval: approval }];
  const waiting = [{ id: 'u-d', displayName: 'Bạn Dũng', by: { id: 'u-y', displayName: 'Chú Tư' }, createdAt: '2026-12-01T09:00:00Z' }];
  const banned = [{ id: 'u-z', displayName: 'Anh Ba', createdAt: '2026-12-01T09:00:00Z' }];
  const mount = (myRole: GroupMember['role'], extra: Record<string, unknown> = {}, approval = false) => {
    const a = fakeApi({
      me: vi.fn(async () => ok(me)), groupMembers: vi.fn(async () => ok(rows(myRole))),
      friends: vi.fn(async () => ok({ friends: [{ gomId: 'lan', displayName: 'Lan' }], incoming: [], outgoing: [] })), conversations: vi.fn(async () => ok(chats(approval))),
      joinRequests: vi.fn(async () => ok({ requests: waiting })), bans: vi.fn(async () => ok({ bans: banned })), ...extra,
    });
    mountGroup(root, a, vi.fn(), G);
    return a;
  };
  const click = (selector: string) => root.querySelector<HTMLElement>(selector)!.click();

  it('shows the approval switch to the leader only, in the state it is in', async () => {
    mount('deputy', {}, true);
    await flush();
    expect(root.querySelector('[data-join-approval]')).toBeNull();
    mount('leader', {}, true);
    await flush();
    expect(root.querySelector<HTMLInputElement>('[data-join-approval]')!.checked).toBe(true);
  });

  it('turns the switch on and off through the server and reloads', async () => {
    const a = mount('leader');
    await flush();
    const box = root.querySelector<HTMLInputElement>('[data-join-approval]')!;
    box.checked = true; box.dispatchEvent(new Event('change', { bubbles: true }));
    await flush();
    expect(a.setJoinApproval).toHaveBeenCalledWith(G, true);
    expect(a.groupMembers).toHaveBeenCalledTimes(2);
  });

  it('lists what waits to leader and deputies, with who asked, and not to a plain member', async () => {
    const leader = mount('leader');
    await flush();
    expect(text(root.querySelector('[data-request="u-d"]')!)).toContain('Bạn Dũng');
    expect(text(root.querySelector('[data-request="u-d"]')!)).toContain('Chú Tư muốn thêm');
    expect(leader.joinRequests).toHaveBeenCalledWith(G);
    mount('deputy');
    await flush();
    expect(root.querySelector('[data-request="u-d"]')).not.toBeNull();
    const member = mount('member');
    await flush();
    expect(root.querySelector('[data-requests]')).toBeNull();
    expect(member.joinRequests).not.toHaveBeenCalled();
  });

  it('approves and declines, then reloads', async () => {
    const a = mount('leader');
    await flush();
    click('[data-request="u-d"] [data-approve]'); await flush();
    expect(a.approveRequest).toHaveBeenCalledWith(G, 'u-d');
    click('[data-request="u-d"] [data-decline]'); await flush();
    expect(a.declineRequest).toHaveBeenCalledWith(G, 'u-d');
    expect(a.groupMembers.mock.calls.length).toBe(3);
  });

  it('says the request was sent when adding in a group that asks for approval', async () => {
    const a = mount('member', { addMember: vi.fn(async () => ok({ status: 'pending' })) });
    await flush();
    click('[data-addable="lan"] button'); await flush();
    expect(a.addMember).toHaveBeenCalledWith(G, 'lan');
    expect(text(root.querySelector('[data-add-status]')!)).toContain('chờ trưởng nhóm hoặc phó nhóm duyệt');
  });

  it('says why when adding fails, for instance for someone who was banned', async () => {
    mount('leader', { addMember: vi.fn(async () => fail('banned', 403)) });
    await flush();
    click('[data-addable="lan"] button'); await flush();
    expect(text(root.querySelector('[role="alert"]')!)).toContain('chặn khỏi nhóm');
    expect(root.querySelector('[data-add-status]')).toBeNull();
  });

  it('says nothing special when the person was added at once', async () => {
    mount('leader');
    await flush();
    click('[data-addable="lan"] button'); await flush();
    expect(root.querySelector('[data-add-status]')).toBeNull();
  });

  it('removes without a ban unless the box is ticked, and with one when it is', async () => {
    const a = mount('leader');
    await flush();
    click('[data-member="u-y"] [data-manage]'); await flush();
    click('[data-remove]'); await flush();
    expect(root.querySelector<HTMLInputElement>('[data-ban]')!.checked).toBe(false);
    click('[data-confirm-sure]'); await flush();
    expect(a.removeMember).toHaveBeenLastCalledWith(G, 'u-y', false);
    click('[data-member="u-y"] [data-manage]'); await flush();
    click('[data-remove]'); await flush();
    const box = root.querySelector<HTMLInputElement>('[data-ban]')!;
    box.checked = true; box.dispatchEvent(new Event('change', { bubbles: true }));
    click('[data-confirm-sure]'); await flush();
    expect(a.removeMember).toHaveBeenLastCalledWith(G, 'u-y', true);
  });

  it('forgets the ban box when the question is cancelled', async () => {
    mount('leader');
    await flush();
    click('[data-member="u-y"] [data-manage]'); await flush();
    click('[data-remove]'); await flush();
    const box = root.querySelector<HTMLInputElement>('[data-ban]')!;
    box.checked = true; box.dispatchEvent(new Event('change', { bubbles: true }));
    click('[data-cancel-sure]'); await flush();
    click('[data-remove]'); await flush();   // the member's menu is still open
    expect(root.querySelector<HTMLInputElement>('[data-ban]')!.checked).toBe(false);
  });

  it('lists who may not come back to leader and deputies, and only the leader lifts it', async () => {
    const leader = mount('leader');
    await flush();
    expect(text(root.querySelector('[data-ban-row="u-z"]')!)).toContain('Anh Ba');
    click('[data-ban-row="u-z"] [data-unban]'); await flush();
    expect(leader.unban).toHaveBeenCalledWith(G, 'u-z');
    mount('deputy');
    await flush();
    expect(root.querySelector('[data-ban-row="u-z"]')).not.toBeNull();
    expect(root.querySelector('[data-unban]')).toBeNull();
    mount('member');
    await flush();
    expect(root.querySelector('[data-bans]')).toBeNull();
  });

  it('says why when the server refuses an approval', async () => {
    mount('leader', { approveRequest: vi.fn(async () => fail('group_full', 409)) });
    await flush();
    click('[data-request="u-d"] [data-approve]'); await flush();
    expect(text(root)).toContain('tối đa');
  });
});
