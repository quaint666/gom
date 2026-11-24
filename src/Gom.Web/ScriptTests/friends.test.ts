import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createApi } from '../Scripts/api.js';
import { mountAdd } from '../Scripts/pages/add.js';
import { mountFriends } from '../Scripts/pages/friends.js';
import { mountHome } from '../Scripts/pages/home.js';
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
  const api = (extra = {}) => fakeApi({
    me: vi.fn(async () => ok(me)), friends: vi.fn(async () => ok(lists)), blocks: vi.fn(async () => ok(blocked)), ...extra,
  });
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
    expect(headings).toEqual(['Bạn bè']);
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
    ['unfriends', '[data-friend="minh"] button[data-action="unfriend"]', 'unfriend', 'minh'],
    ['blocks a friend', '[data-friend="minh"] button[data-action="block"]', 'block', 'minh'],
    ['unblocks', '[data-blocked="bad"] button[data-action="unblock"]', 'unblock', 'bad'],
  ] as const)('%s and reloads the lists', async (_name, selector, method, id) => {
    const a = mount();
    await flush();
    root.querySelector<HTMLButtonElement>(selector)!.click();
    await flush();
    expect(a[method]).toHaveBeenCalledWith(id);
    expect(a.friends).toHaveBeenCalledTimes(2);
  });

  it('opens a conversation with a friend and goes to the home page', async () => {
    const go = vi.fn();
    const a = api({ openDirect: vi.fn(async () => ok({ id: 'c1' })) });
    mountFriends(root, a, go, 'https://gom.test');
    await flush();
    root.querySelector<HTMLButtonElement>('[data-friend="minh"] button[data-action="message"]')!.click();
    await flush();
    expect(a.openDirect).toHaveBeenCalledWith('minh');
    expect(go).toHaveBeenCalledWith('/');
  });

  it('shows a failed action in words', async () => {
    mount(api({ acceptFriend: vi.fn(async () => fail('server', 500)) }));
    await flush();
    root.querySelector<HTMLButtonElement>('[data-incoming="lan"] button[data-action="accept"]')!.click();
    await flush();
    expect(root.querySelector('[role="alert"]')).not.toBeNull();
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

describe('home page conversations', () => {
  const chats = [
    { id: 'c1', kind: 'direct', title: 'Minh', peer: { gomId: 'minh', displayName: 'Minh' }, lastSeq: 0, lastReadSeq: 0 },
  ];
  const api = (list: unknown[]) => fakeApi({ me: vi.fn(async () => ok(me)), conversations: vi.fn(async () => ok(list)) });

  it('shows the empty state with a way to add friends', async () => {
    mountHome(root, api([]), vi.fn());
    await flush();
    expect(text(root)).toContain('Chưa có cuộc trò chuyện nào');
    expect(root.querySelector('a[href="/friends"]')).not.toBeNull();
  });

  it('lists conversations by the other person\'s name', async () => {
    mountHome(root, api(chats), vi.fn());
    await flush();
    expect(text(root.querySelector('[data-chat="c1"]')!)).toContain('Minh');
    expect(text(root)).not.toContain('Chưa có cuộc trò chuyện nào');
  });

  it('always has a way to the friends page', async () => {
    mountHome(root, api(chats), vi.fn());
    await flush();
    expect(root.querySelector('header a[href="/friends"]')).not.toBeNull();
  });
});
