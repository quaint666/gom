import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createApi } from '../Scripts/api.js';
import { mountAdd } from '../Scripts/pages/add.js';
import { mountFriends } from '../Scripts/pages/friends.js';
import { mountSignIn } from '../Scripts/pages/signin.js';
import { safeNext } from '../Scripts/pages/signin.js';
import { errorText } from '../Scripts/text.js';
import { fail, fakeApi, fill, flush, ok, submit, text } from './helpers.js';

let root: HTMLElement;
beforeEach(() => { root = document.createElement('div'); document.body.replaceChildren(root); });

const me = { gomId: 'mom', displayName: 'Mẹ', isOwner: false };
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const calls = (fetchFn: ReturnType<typeof vi.fn>) =>
  fetchFn.mock.calls.map((c) => [(c as unknown as [string, RequestInit])[0], (c as unknown as [string, RequestInit])[1].method]);

describe('api: friends, blocks and conversations', () => {
  it('uses the right method and path for each call', async () => {
    const fetchFn = vi.fn(async () => new Response(null, { status: 204 }));
    const api = createApi(fetchFn);
    await api.friends(); await api.askFriend('minh'); await api.acceptFriend('minh'); await api.withdrawFriend('minh'); await api.unfriend('minh');
    await api.blocks(); await api.block('minh'); await api.unblock('minh'); await api.conversations(); await api.openDirect('minh');
    expect(calls(fetchFn)).toEqual([
      ['/api/friends', 'GET'], ['/api/friends/requests', 'POST'], ['/api/friends/requests/minh/accept', 'POST'],
      ['/api/friends/requests/minh', 'DELETE'], ['/api/friends/minh', 'DELETE'], ['/api/blocks', 'GET'], ['/api/blocks/minh', 'POST'],
      ['/api/blocks/minh', 'DELETE'], ['/api/conversations', 'GET'], ['/api/conversations/direct', 'POST'],
    ]);
  });

  it('sends the ID in the body for a request and for opening a conversation, and encodes it in a path', async () => {
    const fetchFn = vi.fn(async () => json({ status: 'pending' }));
    const api = createApi(fetchFn);
    await api.askFriend('minh');
    await api.openDirect('minh');
    await api.unfriend('a/b');
    const bodies = fetchFn.mock.calls.map((c) => (c as unknown as [string, RequestInit])[1].body);
    expect(bodies.map((b) => (b === undefined ? undefined : JSON.parse(b as string)))).toEqual([{ gomId: 'minh' }, { gomId: 'minh' }, undefined]);
    expect(calls(fetchFn)[2]![0]).toBe('/api/friends/a%2Fb');
  });

  it('knows the new error codes and says each in words', async () => {
    for (const code of ['user_not_found', 'cannot_add_self', 'already_friends', 'you_blocked_them', 'not_friends', 'cannot_chat_self'] as const) {
      const result = await createApi(async () => json({ error: code }, 400)).askFriend('x');
      expect(result).toMatchObject({ ok: false, error: code });
      expect(errorText(fail(code)).length).toBeGreaterThan(10);
      expect(errorText(fail(code))).not.toBe(errorText(fail('unknown')));
    }
  });
});

describe('sign-in remembers where you were going', () => {
  it.each([
    ['/add/mom', '/add/mom'], ['/friends', '/friends'], [null, '/'], ['', '/'], ['//evil.test', '/'], ['https://evil.test', '/'],
    ['/\\evil.test', '/'], ['/a\\b', '/'], ['javascript:alert(1)', '/'], ['add/mom', '/'], ['/a b', '/'],
  ])('%s becomes %s', (given, expected) => { expect(safeNext(given)).toBe(expected); });

  it('goes there after signing in', async () => {
    const api = fakeApi({ signIn: vi.fn(async () => ok(me)) });
    const go = vi.fn();
    mountSignIn(root, api, go, '/add/minh');
    fill(root, 'input[name="gomId"]', 'mom');
    fill(root, 'input[type="password"]', 'secret password');
    submit(root);
    await flush();
    expect(go).toHaveBeenCalledWith('/add/minh');
  });

  it('never goes to an address outside the site', async () => {
    const api = fakeApi({ signIn: vi.fn(async () => ok(me)) });
    const go = vi.fn();
    mountSignIn(root, api, go, '//evil.test');
    fill(root, 'input[name="gomId"]', 'mom');
    fill(root, 'input[type="password"]', 'secret password');
    submit(root);
    await flush();
    expect(go).toHaveBeenCalledWith('/');
  });
});

describe('friends page', () => {
  const lists = {
    friends: [{ gomId: 'minh', displayName: 'Minh' }],
    incoming: [{ gomId: 'lan', displayName: 'Lan', createdAt: '2026-12-01T09:00:00Z' }],
    outgoing: [{ gomId: 'hoa', displayName: 'Hoa', createdAt: '2026-12-01T09:00:00Z' }],
  };
  const blocked = [{ gomId: 'bad', displayName: 'Bad' }];
  const chatsList = [
    { id: 'g1', kind: 'group', title: 'Gia đình', peer: null, lastSeq: 0, lastReadSeq: 0, unread: 0, lastMessage: null, memberCount: 4 },
    { id: 'd1', kind: 'direct', title: 'Minh', peer: { gomId: 'minh', displayName: 'Minh' }, lastSeq: 0, lastReadSeq: 0, unread: 0, lastMessage: null },
  ];
  const api = (extra = {}) => fakeApi({
    me: vi.fn(async () => ok(me)), friends: vi.fn(async () => ok(lists)), blocks: vi.fn(async () => ok(blocked)),
    conversations: vi.fn(async () => ok(chatsList)), ...extra,
  });
  /** The "more" tray of a friend (unfriend, block) opens first. */
  const tray = (id: string) => { root.querySelector<HTMLButtonElement>(`[data-friend="${id}"] button[data-more]`)!.click(); };
  const mount = (a = api()) => { mountFriends(root, a, vi.fn(), 'https://gom.test'); return a; };

  it('sends a signed-out visitor to sign in', async () => {
    const go = vi.fn();
    mountFriends(root, fakeApi(), go, 'https://gom.test');
    await flush();
    expect(go).toHaveBeenCalledWith('/sign-in');
  });

  it('lists friends, requests received, requests sent and blocked people, each in its own section', async () => {
    mount();
    await flush();
    expect(text(root.querySelector('[data-friend="minh"]')!)).toContain('Minh');
    expect(text(root.querySelector('[data-incoming="lan"]')!)).toContain('Lan');
    expect(text(root.querySelector('[data-outgoing="hoa"]')!)).toContain('Hoa');
    expect(text(root.querySelector('[data-blocked="bad"]')!)).toContain('Bad');
  });

  it('hides a section with nothing in it, and says so when there are no friends yet', async () => {
    mount(api({
      friends: vi.fn(async () => ok({ friends: [], incoming: [], outgoing: [] })),
      blocks: vi.fn(async () => ok([])),
    }));
    await flush();
    const headings = [...root.querySelectorAll('h2.section')].map((h) => text(h));
    expect(headings).toEqual([]);
    expect(text(root)).toContain('Chưa có bạn bè nào');
  });

  it('shows your own add-me link, to copy', async () => {
    mount();
    await flush();
    const field = root.querySelector<HTMLInputElement>('input[data-mylink]')!;
    expect(field.value).toBe('https://gom.test/add/mom');
    expect(field.readOnly).toBe(true);
    const writeText = vi.fn(async () => undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    root.querySelector<HTMLButtonElement>('button[data-copy]')!.click();
    await flush();
    expect(writeText).toHaveBeenCalledWith('https://gom.test/add/mom');
    expect(text(root.querySelector('[data-copy-status]')!)).toContain('Đã sao chép');
  });

  it('has an ID field that is not autofilled as a username and not autocorrected', async () => {
    mount();
    await flush();
    const input = root.querySelector<HTMLInputElement>('input[name="friendId"]')!;
    expect(input.getAttribute('autocomplete')).toBe('off');
    expect(input.getAttribute('autocapitalize')).toBe('none');
    expect(input.getAttribute('spellcheck')).toBe('false');
    expect(root.querySelector(`label[for="${input.id}"]`)).not.toBeNull();
  });

  it('asks by ID, says what happened, clears the field and reloads', async () => {
    const a = api({ askFriend: vi.fn(async () => ok({ status: 'pending' })) });
    mount(a);
    await flush();
    fill(root, 'input[name="friendId"]', ' Cuong ');
    submit(root);
    await flush();
    expect(a.askFriend).toHaveBeenCalledWith('Cuong');
    expect(text(root.querySelector('[data-add-status]')!)).toContain('Đã gửi lời mời');
    expect(root.querySelector<HTMLInputElement>('input[name="friendId"]')!.value).toBe('');
    expect(a.friends).toHaveBeenCalledTimes(2);
  });

  it('says "you are now friends" when the other had already asked', async () => {
    mount(api({ askFriend: vi.fn(async () => ok({ status: 'friends' })) }));
    await flush();
    fill(root, 'input[name="friendId"]', 'lan');
    submit(root);
    await flush();
    expect(text(root.querySelector('[data-add-status]')!)).toContain('đã là bạn bè');
  });

  it('shows a refused request in words and keeps what was typed', async () => {
    mount(api({ askFriend: vi.fn(async () => fail('user_not_found', 404)) }));
    await flush();
    fill(root, 'input[name="friendId"]', 'nobody');
    submit(root);
    await flush();
    expect(text(root.querySelector('[role="alert"]')!)).toContain('Không tìm thấy');
    expect(root.querySelector<HTMLInputElement>('input[name="friendId"]')!.value).toBe('nobody');
  });

  it('does not send an empty ID', async () => {
    const a = mount();
    await flush();
    submit(root);
    await flush();
    expect(a.askFriend).not.toHaveBeenCalled();
  });

  it.each([
    ['accepts', '[data-incoming="lan"] button[data-action="accept"]', 'acceptFriend', 'lan'],
    ['declines', '[data-incoming="lan"] button[data-action="decline"]', 'withdrawFriend', 'lan'],
    ['cancels', '[data-outgoing="hoa"] button[data-action="cancel"]', 'withdrawFriend', 'hoa'],
    ['unblocks', '[data-blocked="bad"] button[data-action="unblock"]', 'unblock', 'bad'],
  ] as const)('%s and reloads the lists', async (_name, selector, method, id) => {
    const a = mount();
    await flush();
    root.querySelector<HTMLButtonElement>(selector)!.click();
    await flush();
    expect(a[method]).toHaveBeenCalledWith(id);
    expect(a.friends).toHaveBeenCalledTimes(2);
  });

  it.each([
    ['unfriending', 'unfriend', 'Xóa bạn với'],
    ['blocking', 'block', 'Chặn'],
  ] as const)('asks before %s, names the person, and does nothing until yes', async (_name, method, words) => {
    const a = mount();
    await flush();
    tray('minh'); await flush();
    root.querySelector<HTMLButtonElement>(`[data-friend="minh"] button[data-action="${method}"]`)!.click();
    await flush();
    expect(text(root.querySelector('[data-sure="minh"]')!)).toContain(words);
    expect(text(root.querySelector('[data-sure="minh"]')!)).toContain('Minh');
    expect(a[method]).not.toHaveBeenCalled();
    root.querySelector<HTMLButtonElement>('[data-confirm-sure]')!.click();
    await flush();
    expect(a[method]).toHaveBeenCalledWith('minh');
    expect(a.friends).toHaveBeenCalledTimes(2);
    expect(root.querySelector('[data-sure]')).toBeNull();
  });

  it('asks only under the friend you tapped, not under the others', async () => {
    const two = { ...lists, friends: [{ gomId: 'minh', displayName: 'Minh' }, { gomId: 'cuong', displayName: 'Cường' }] };
    mount(api({ friends: vi.fn(async () => ok(two)) }));
    await flush();
    tray('cuong'); await flush();
    root.querySelector<HTMLButtonElement>('[data-friend="cuong"] button[data-action="block"]')!.click();
    await flush();
    expect(root.querySelectorAll('[data-sure]')).toHaveLength(1);
    expect(root.querySelector('[data-sure="cuong"]')).not.toBeNull();
    expect(text(root.querySelector('[data-sure]')!)).toContain('Cường');
  });

  it.each(['unfriend', 'block'] as const)('drops the question on "Hủy" and %s is not called', async (method) => {
    const a = mount();
    await flush();
    tray('minh'); await flush();
    root.querySelector<HTMLButtonElement>(`[data-friend="minh"] button[data-action="${method}"]`)!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('[data-cancel-sure]')!.click();
    await flush();
    expect(a[method]).not.toHaveBeenCalled();
    expect(root.querySelector('[data-sure]')).toBeNull();
  });

  it('opens a conversation with a friend and goes to it', async () => {
    const go = vi.fn();
    const a = api({ openDirect: vi.fn(async () => ok({ id: 'c1' })) });
    mountFriends(root, a, go, 'https://gom.test');
    await flush();
    root.querySelector<HTMLButtonElement>('[data-friend="minh"] button[data-action="message"]')!.click();
    await flush();
    expect(a.openDirect).toHaveBeenCalledWith('minh');
    expect(go).toHaveBeenCalledWith('/c/c1');
  });

  it('shows a failed action in words', async () => {
    mount(api({ acceptFriend: vi.fn(async () => fail('server', 500)) }));
    await flush();
    root.querySelector<HTMLButtonElement>('[data-incoming="lan"] button[data-action="accept"]')!.click();
    await flush();
    expect(root.querySelector('[role="alert"]')).not.toBeNull();
  });
});

describe('contacts: tabs, a handful of rows, and search', () => {
  const many = Array.from({ length: 8 }, (_, i) => ({ gomId: `f${i}`, displayName: i === 0 ? 'Đào Thị Lan' : `Bạn ${i}` }));
  const lists = { friends: many, incoming: [], outgoing: [] };
  const chatsList = [
    { id: 'g1', kind: 'group', title: 'Gia đình', peer: null, lastSeq: 0, lastReadSeq: 0, unread: 0, lastMessage: null, memberCount: 4 },
    { id: 'd1', kind: 'direct', title: 'Bạn 1', peer: { gomId: 'f1', displayName: 'Bạn 1' }, lastSeq: 0, lastReadSeq: 0, unread: 0, lastMessage: null },
  ];
  const mount = () => {
    mountFriends(root, fakeApi({
      me: vi.fn(async () => ok({ id: 'u1', gomId: 'mom', displayName: 'Mẹ', isOwner: false })), friends: vi.fn(async () => ok(lists)),
      blocks: vi.fn(async () => ok([])), conversations: vi.fn(async () => ok(chatsList)),
    }), vi.fn(), 'https://gom.test');
  };
  const rows = () => root.querySelectorAll('[data-friend]').length;
  const type = (value: string) => { const f = root.querySelector<HTMLInputElement>('input[name="find"]')!; f.value = value; f.dispatchEvent(new Event('input', { bubbles: true })); };

  it('shows a handful of friends and the rest behind "Xem thêm"', async () => {
    mount();
    await flush();
    expect(rows()).toBe(5);
    expect(text(root.querySelector('[data-more-rows]')!)).toContain('Xem thêm (3)');
    root.querySelector<HTMLButtonElement>('[data-more-rows]')!.click();
    await flush();
    expect(rows()).toBe(8);
    expect(root.querySelector('[data-more-rows]')).toBeNull();
  });

  it('searches every friend, not only the handful, and ignores accents and capitals', async () => {
    mount();
    await flush();
    type('dao lan'); await flush();
    expect([...root.querySelectorAll('[data-friend]')].map((r) => r.getAttribute('data-friend'))).toEqual(['f0']);
    type('bạn 7'); await flush();
    expect([...root.querySelectorAll('[data-friend]')].map((r) => r.getAttribute('data-friend'))).toEqual(['f7']);
    type('không có ai'); await flush();
    expect(rows()).toBe(0);
    expect(text(root.querySelector('[data-empty]')!)).toContain('Không tìm thấy');
  });

  it('has a Groups tab with only the groups, and a way to make a new one', async () => {
    mount();
    await flush();
    root.querySelector<HTMLButtonElement>('[data-tab="groups"]')!.click();
    await flush();
    expect(rows()).toBe(0);
    expect(root.querySelectorAll('[data-group]')).toHaveLength(1);
    expect(root.querySelector('[data-group="g1"]')!.getAttribute('href')).toBe('/c/g1');
    expect(text(root.querySelector('[data-group="g1"]')!)).toContain('4 thành viên');
    expect(root.querySelector('a[data-new-group]')!.getAttribute('href')).toBe('/groups/new');
    expect(root.querySelector('[data-tab="groups"]')!.getAttribute('aria-selected')).toBe('true');
  });

  it('keeps the add-friend form inside a closed box, in the page for the keyboard and a screen reader', async () => {
    mount();
    await flush();
    const box = root.querySelector<HTMLDetailsElement>('details[data-addbox]')!;
    expect(box.open).toBe(false);
    expect(box.querySelector('form[data-add]')).not.toBeNull();
  });
});

describe('add-by-link page', () => {
  const api = (extra = {}) => fakeApi({ me: vi.fn(async () => ok(me)), ...extra });

  it('sends a signed-out visitor to sign in and back here afterwards', async () => {
    const go = vi.fn();
    mountAdd(root, fakeApi(), go, 'minh');
    await flush();
    expect(go).toHaveBeenCalledWith('/sign-in?next=%2Fadd%2Fminh');
  });

  it('does nothing until the button is pressed (a link must not change anything)', async () => {
    const a = api();
    mountAdd(root, a, vi.fn(), 'minh');
    await flush();
    expect(a.askFriend).not.toHaveBeenCalled();
    expect(text(root)).toContain('@minh');
  });

  it('sends the request when asked and says so', async () => {
    const a = api({ askFriend: vi.fn(async () => ok({ status: 'pending' })) });
    mountAdd(root, a, vi.fn(), 'minh');
    await flush();
    root.querySelector<HTMLButtonElement>('button[data-send]')!.click();
    await flush();
    expect(a.askFriend).toHaveBeenCalledWith('minh');
    expect(text(root)).toContain('Đã gửi lời mời');
    expect(root.querySelector('button[data-send]')).toBeNull();
    expect(root.querySelector('a[href="/friends"]')).not.toBeNull();
  });

  it('says in words when the ID is unknown or is your own', async () => {
    mountAdd(root, api({ askFriend: vi.fn(async () => fail('user_not_found', 404)) }), vi.fn(), 'ghost');
    await flush();
    root.querySelector<HTMLButtonElement>('button[data-send]')!.click();
    await flush();
    expect(text(root.querySelector('[role="alert"]')!)).toContain('Không tìm thấy');
  });
});
