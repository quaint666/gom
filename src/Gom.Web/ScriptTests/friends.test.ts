import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createApi } from '../Scripts/api.js';
import { mountAdd } from '../Scripts/pages/add.js';
import { avatarTone } from '../Scripts/chat/model.js';
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
  /** A friend's "⋯" menu (unfriend, block) opens first; its actions are in `[data-row-actions]`, outside the row. */
  const tray = (id: string) => { root.querySelector<HTMLButtonElement>(`[data-friend="${id}"] button[data-more]`)!.click(); };
  const choose = (action: string) => { root.querySelector<HTMLButtonElement>(`[data-row-actions] button[data-${action}]`)!.click(); };
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
    choose(method);
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

  it('opens unfriend and block in a menu like the chat list\'s, which closes back to the friend\'s "⋯"', async () => {
    const a = mount();
    await flush();
    const more = root.querySelector<HTMLButtonElement>('[data-friend="minh"] button[data-more]')!;
    expect(more.getAttribute('aria-haspopup')).toBe('menu');
    expect(root.querySelector('[data-friend="minh"] [data-unfriend]')).toBeNull();
    tray('minh'); await flush();
    const menu = root.querySelector<HTMLElement>('[data-row-actions]')!;
    expect(menu.getAttribute('aria-label')).toBe('Tùy chọn cho Minh');
    expect([...menu.querySelectorAll('[role="menuitem"]')].map((b) => text(b))).toEqual(['Nhắn tin', 'Tạo nhóm với Minh', 'Xóa bạn', 'Chặn']);
    expect(document.activeElement).toBe(menu.querySelector('[data-message]'));
    root.querySelector<HTMLElement>('[data-scrim]')!.click(); await flush();
    expect(root.querySelector('[data-row-actions]')).toBeNull();
    expect(document.activeElement).toBe(root.querySelector('[data-friend="minh"] button[data-more]'));
    expect(a.unfriend).not.toHaveBeenCalled();
  });

  describe('a friend\'s menu from a long press, a right-click and its choices', () => {
    const press = (type: string, x = 5, y = 5) => root.querySelector('[data-friend="minh"]')!.dispatchEvent(
      Object.assign(new Event(type, { bubbles: true }), { pointerType: 'touch', clientX: x, clientY: y }));
    const menuOpen = () => root.querySelector('[data-row-actions]') !== null;

    it('opens after a finger is held for a second, and not before', async () => {
      vi.useFakeTimers();
      try {
        mount(); await vi.advanceTimersByTimeAsync(0);
        press('pointerdown');
        await vi.advanceTimersByTimeAsync(900);
        expect(menuOpen()).toBe(false);
        await vi.advanceTimersByTimeAsync(150);
        expect(menuOpen()).toBe(true);
      } finally { vi.useRealTimers(); }
    });

    it.each([['lifted', 'pointerup'], ['scrolling', 'pointermove']])('does not open when the finger is %s first', async (_n, type) => {
      vi.useFakeTimers();
      try {
        mount(); await vi.advanceTimersByTimeAsync(0);
        press('pointerdown');
        press(type, 5, type === 'pointermove' ? 40 : 5);
        await vi.advanceTimersByTimeAsync(1500);
        expect(menuOpen()).toBe(false);
      } finally { vi.useRealTimers(); }
    });

    it('opens on a right-click, with the browser\'s own menu held back', async () => {
      mount(); await flush();
      const e = new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 10, clientY: 10 });
      root.querySelector('[data-friend="minh"]')!.dispatchEvent(e);
      await flush();
      expect(e.defaultPrevented).toBe(true);
      expect(menuOpen()).toBe(true);
    });

    it('"Nhắn tin" opens the one-to-one chat and "Tạo nhóm với" starts a group with them', async () => {
      const go = vi.fn();
      const a = api({ openDirect: vi.fn(async () => ok({ id: 'd1' })) });
      mountFriends(root, a, go, 'https://gom.test'); await flush();
      tray('minh'); await flush(); choose('message'); await flush();
      expect(a.openDirect).toHaveBeenCalledWith('minh');
      expect(go).toHaveBeenCalledWith('/c/d1');
      tray('minh'); await flush(); choose('group-with'); await flush();
      expect(go).toHaveBeenLastCalledWith('/groups/new?with=minh');
    });
  });

  it('asks only under the friend you tapped, not under the others', async () => {
    const two = { ...lists, friends: [{ gomId: 'minh', displayName: 'Minh' }, { gomId: 'cuong', displayName: 'Cường' }] };
    mount(api({ friends: vi.fn(async () => ok(two)) }));
    await flush();
    tray('cuong'); await flush();
    choose('block');
    await flush();
    expect(root.querySelectorAll('[data-sure]')).toHaveLength(1);
    expect(root.querySelector('[data-sure="cuong"]')).not.toBeNull();
    expect(text(root.querySelector('[data-sure]')!)).toContain('Cường');
  });

  it.each(['unfriend', 'block'] as const)('drops the question on "Hủy" and %s is not called', async (method) => {
    const a = mount();
    await flush();
    tray('minh'); await flush();
    choose(method);
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

describe('contacts: tabs, recent and A to Z, and search', () => {
  const many = Array.from({ length: 8 }, (_, i) => ({ gomId: `f${i}`, displayName: i === 0 ? 'Đào Thị Lan' : `Bạn ${i}` }));
  const lists = { friends: many, incoming: [], outgoing: [] };
  /** A one-to-one conversation with friend `i`, last written in at minute `minute` (f0 and f7 have none). */
  const direct = (i: number, minute: number) => ({ id: `d${i}`, kind: 'direct', title: `Bạn ${i}`, peer: { gomId: `f${i}`, displayName: `Bạn ${i}` }, lastSeq: 1, lastReadSeq: 1, unread: 0,
    lastMessage: { seq: 1, body: 'chào', createdAt: `2026-11-27T10:${String(minute).padStart(2, '0')}:00Z`, sender: { id: 'x', displayName: 'x' } } });
  const group = (id: string, title: string, minute: number | null) => ({ id, kind: 'group', title, peer: null, lastSeq: 0, lastReadSeq: 0, unread: 0, memberCount: 4,
    lastMessage: minute === null ? null : { seq: 1, body: 'chào', createdAt: `2026-11-27T10:${String(minute).padStart(2, '0')}:00Z`, sender: { id: 'x', displayName: 'x' } } });
  const chatsList = [group('g1', 'Gia đình', 30), direct(1, 1), direct(2, 9), direct(3, 3), direct(4, 4), direct(5, 5), direct(6, 6)];
  const mount = (friends = many, chats: unknown[] = chatsList) => {
    mountFriends(root, fakeApi({
      me: vi.fn(async () => ok({ id: 'u1', gomId: 'mom', displayName: 'Mẹ', isOwner: false })), friends: vi.fn(async () => ok({ ...lists, friends })),
      blocks: vi.fn(async () => ok([])), conversations: vi.fn(async () => ok(chats)),
    }), vi.fn(), 'https://gom.test');
  };
  /** The rows under a heading, up to the next heading of its kind. */
  const after = (selector: string) => [...(root.querySelector(selector)?.nextElementSibling?.querySelectorAll('[data-friend], [data-group]') ?? [])]
    .map((r) => r.getAttribute('data-friend') ?? r.getAttribute('data-group'));
  const everyone = () => [...root.querySelectorAll('[data-letter] + ul [data-friend]')].map((r) => r.getAttribute('data-friend'));
  const type = (value: string) => { const f = root.querySelector<HTMLInputElement>('input[name="find"]')!; f.value = value; f.dispatchEvent(new Event('input', { bubbles: true })); };

  it('opens with "Gần đây", the five talked with last, newest first, then everyone A to Z, with no "Xem thêm"', async () => {
    mount();
    await flush();
    expect(text(root.querySelector('[data-recent]')!)).toBe('Gần đây');
    expect(after('[data-recent]')).toEqual(['f2', 'f6', 'f5', 'f4', 'f3']);   // f1 is the sixth; f0 and f7 were never written to
    expect(text(root.querySelector('[data-all]')!)).toBe('Tất cả · 8');
    expect(everyone()).toEqual(['f1', 'f2', 'f3', 'f4', 'f5', 'f6', 'f7', 'f0']);   // Đào Thị Lan last, under D
    expect([...root.querySelectorAll('[data-letter]')].map((h) => h.getAttribute('data-letter'))).toEqual(['B', 'D']);
    expect(root.querySelector('[data-more-rows]')).toBeNull();
  });

  it('has no "Gần đây" with five friends or fewer, only everyone A to Z', async () => {
    mount(many.slice(0, 5));
    await flush();
    expect(root.querySelector('[data-recent]')).toBeNull();
    expect(root.querySelector('[data-all]')).toBeNull();
    expect(everyone()).toEqual(['f1', 'f2', 'f3', 'f4', 'f0']);
  });

  it('searches every friend and shows only what matches, A to Z, ignoring accents and capitals', async () => {
    mount();
    await flush();
    type('dao lan'); await flush();
    expect([...root.querySelectorAll('[data-friend]')].map((r) => r.getAttribute('data-friend'))).toEqual(['f0']);
    type('bạn'); await flush();   // seven match
    expect(root.querySelector('[data-recent]')).toBeNull();
    expect(everyone()).toEqual(['f1', 'f2', 'f3', 'f4', 'f5', 'f6', 'f7']);
    type('không có ai'); await flush();
    expect(root.querySelectorAll('[data-friend]')).toHaveLength(0);
    expect(text(root.querySelector('[data-empty]')!)).toContain('Không tìm thấy');
  });

  it('does the same for groups: the five written in last, then all of them A to Z', async () => {
    const groups = [group('g1', 'Gia đình', 30), group('g2', 'Bạn đại học', 1), group('g3', 'Xóm trọ', 20), group('g4', 'Lớp 12A1', 10), group('g5', 'Họ Nguyễn', 40), group('g6', 'Câu cá', null)];
    mount(many, groups);
    await flush();
    root.querySelector<HTMLButtonElement>('[data-tab="groups"]')!.click(); await flush();
    expect(after('[data-recent]')).toEqual(['g5', 'g1', 'g3', 'g4', 'g2']);
    expect(text(root.querySelector('[data-all]')!)).toBe('Tất cả · 6');
    expect(after('[data-all]')).toEqual(['g2', 'g6', 'g1', 'g5', 'g4', 'g3']);
  });

  it('gives each friend the avatar colour of their name', async () => {
    mount();
    await flush();
    const row = root.querySelector('[data-friend="f1"] .av')!;   // "Bạn 1"
    expect(row.classList.contains(`a${avatarTone('Bạn 1')}`)).toBe(true);
    expect(row.textContent).toBe('B1');
  });

  it('has a Groups tab with only the groups, and a way to make a new one', async () => {
    mount();
    await flush();
    root.querySelector<HTMLButtonElement>('[data-tab="groups"]')!.click();
    await flush();
    expect(root.querySelectorAll('[data-friend]')).toHaveLength(0);
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
