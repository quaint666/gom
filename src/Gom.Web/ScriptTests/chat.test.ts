import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ChatEvent, ChatMessage, Conversation } from '../Scripts/api.js';
import type { LiveControl, LiveHandlers } from '../Scripts/chat/live.js';
import { mountChat, type ChatDeps } from '../Scripts/pages/chat.js';
import { fail, fakeApi, fill, flush, ok, text } from './helpers.js';

let root: HTMLElement;
beforeEach(() => { root = document.createElement('div'); document.body.replaceChildren(root); });

const me = { gomId: 'mom', displayName: 'Mẹ', isOwner: false };
const C1 = '11111111-1111-4111-8111-111111111111';
const C2 = '22222222-2222-4222-8222-222222222222';
const NOW = new Date(Date.UTC(2026, 11, 10, 12, 30));
const at = (hh: number, mm = 0) => new Date(Date.UTC(2026, 11, 10, hh, mm)).toISOString();

const msg = (seq: number, from: 'mom' | 'minh', body = `m${seq}`, when = at(10, seq), conv = C1): ChatMessage => ({
  id: `id${seq}`, seq, conversationId: conv, clientId: `cl${seq}`, sender: from === 'mom' ? { gomId: 'mom', displayName: 'Mẹ' } : { gomId: 'minh', displayName: 'Minh' }, body, createdAt: when,
});
const created = (m: ChatMessage): ChatEvent => ({ seq: m.seq, kind: 'created', message: m });

const chat = (id: string, name: string, extra: Partial<Conversation> = {}): Conversation => ({
  id, kind: 'direct', title: name, peer: { gomId: name.toLowerCase(), displayName: name }, lastSeq: 0, lastReadSeq: 0, unread: 0, lastMessage: null, ...extra,
});
const withLast = (m: ChatMessage, extra: Partial<Conversation> = {}) =>
  chat(m.conversationId, m.sender.displayName, { lastSeq: m.seq, lastMessage: { seq: m.seq, body: m.body, createdAt: m.createdAt, sender: m.sender }, ...extra });

interface Harness {
  api: ReturnType<typeof fakeApi>;
  handlers: () => LiveHandlers;
  live: { start: ReturnType<typeof vi.fn>; stop: ReturnType<typeof vi.fn>; sync: ReturnType<typeof vi.fn>; retryNow: ReturnType<typeof vi.fn> };
  nav: { path: string; push: ReturnType<typeof vi.fn>; pop: () => void };
  go: ReturnType<typeof vi.fn>;
  visible: { value: boolean };
  coarse: { value: boolean };
  timers: { ms: number; fn: () => void; live: boolean }[];
  outbox: { items: { conversation: string; clientId: string; body: string; createdAt: string }[]; saves: number };
}

function start(opts: { outbox?: Harness['outbox']['items']; path?: string; chats?: Conversation[]; history?: Record<string, ChatMessage[]>; hasMore?: boolean; api?: Record<string, unknown> } = {}): Harness {
  const history = opts.history ?? {};
  const api = fakeApi({
    me: vi.fn(async () => ok(me)),
    // A fresh copy each time, as a real server gives: the page changes what it is handed.
    conversations: vi.fn(async () => ok(structuredClone(opts.chats ?? []))),
    history: vi.fn(async (id: string) => ok({ messages: [...(history[id] ?? [])].reverse(), hasMore: opts.hasMore ?? false })),
    ...(opts.api ?? {}),
  });
  let captured: LiveHandlers | null = null;
  const live = { start: vi.fn(), stop: vi.fn(), sync: vi.fn(), retryNow: vi.fn() };
  let popCallback: () => void = () => undefined;
  const nav = {
    path: opts.path ?? '/',
    push: vi.fn((p: string) => { nav.path = p; }),
    pop: () => popCallback(),
  };
  const visible = { value: true };
  const coarse = { value: false };
  const go = vi.fn();
  let n = 0;
  const timers: Harness['timers'] = [];
  const outbox: Harness['outbox'] = { items: opts.outbox ?? [], saves: 0 };
  const deps: ChatDeps = {
    timer: {
      set: (fn, ms) => {
        const entry = { ms, live: true, fn: () => { if (!entry.live) return; entry.live = false; fn(); } };
        timers.push(entry);
        return timers.length;
      },
      clear: (id) => { const t = timers[id - 1]; if (t) t.live = false; },
    },
    outbox: () => ({ load: () => outbox.items.map((i) => ({ ...i })), save: (items) => { outbox.items = items.map((i) => ({ ...i })); outbox.saves++; } }),
    live: (h) => { captured = h; return live as unknown as LiveControl; },
    nav: { path: () => nav.path, push: nav.push, onPop: (cb) => { popCallback = cb; } },
    now: () => NOW,
    newId: () => `new-${++n}`,
    coarsePointer: () => coarse.value,
    visible: () => visible.value,
  };
  mountChat(root, api, go, deps);
  return { api, handlers: () => captured!, live, nav, go, visible, coarse, timers, outbox };
}

const rows = () => [...root.querySelectorAll<HTMLElement>('[data-chat]')];
const bubbles = () => [...root.querySelectorAll<HTMLElement>('[data-msg]')];
const compose = (value: string) => fill(root, 'textarea[name="message"]', value);
const submitForm = () => root.querySelector('form.composer')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));

describe('chat: signed out and loading', () => {
  it('sends a signed-out visitor to sign in', async () => {
    const h = start({ api: { me: vi.fn(async () => fail('unauthorized', 401)) } });
    await flush();
    expect(h.go).toHaveBeenCalledWith('/sign-in');
  });

  it('goes to sign-in when the list says the session is gone', async () => {
    const h = start({ api: { conversations: vi.fn(async () => fail('unauthorized', 401)) } });
    await flush();
    expect(h.go).toHaveBeenCalledWith('/sign-in');
  });

  it('connects to the live socket once signed in, and not before', async () => {
    const h = start({ api: { me: vi.fn(async () => fail('unauthorized', 401)) } });
    await flush();
    expect(h.live.start).not.toHaveBeenCalled();
    const ok2 = start();
    await flush();
    expect(ok2.live.start).toHaveBeenCalledOnce();
  });

  it('shows a failure in words', async () => {
    start({ api: { conversations: vi.fn(async () => fail('server', 500)) } });
    await flush();
    expect(root.querySelector('[role="alert"]')).not.toBeNull();
  });
});

describe('chat: the list', () => {
  it('shows the empty state with a way to add friends', async () => {
    start();
    await flush();
    expect(text(root)).toContain('Chưa có cuộc trò chuyện nào');
    expect(root.querySelector('a[href="/friends"].btn')).not.toBeNull();
  });

  it('shows each conversation with the other person, the last message, the time and the unread count', async () => {
    const m = msg(3, 'minh', 'Tối nay ăn cơm nhé', at(12, 10));
    start({ chats: [withLast(m, { unread: 2 }), chat(C2, 'Lan')] });
    await flush();
    const [first, second] = rows();
    expect(text(first!)).toContain('Minh');
    expect(text(first!)).toContain('Tối nay ăn cơm nhé');
    expect(first!.querySelector('.av')!.textContent).toBe('M');
    expect(first!.querySelector('.tm')!.textContent).toMatch(/^\d{2}:\d{2}$/);
    expect(first!.querySelector('.badge')!.textContent).toBe('2');
    expect(first!.querySelector('.badge')!.getAttribute('aria-label')).toBe('2 tin chưa đọc');
    expect(first!.classList.contains('unread')).toBe(true);
    expect(text(second!)).toContain('Chưa có tin nhắn');
    expect(second!.querySelector('.badge')).toBeNull();
  });

  it('says "Bạn:" before a message you wrote yourself', async () => {
    start({ chats: [withLast(msg(1, 'mom', 'Mẹ nhắn', at(12)), { peer: { gomId: 'minh', displayName: 'Minh' }, title: 'Minh' })] });
    await flush();
    expect(text(rows()[0]!.querySelector('.pv')!)).toBe('Bạn: Mẹ nhắn');
  });

  it('shows 99+ for a very long unread count', async () => {
    start({ chats: [withLast(msg(1, 'minh'), { unread: 250 })] });
    await flush();
    expect(rows()[0]!.querySelector('.badge')!.textContent).toBe('99+');
  });

  it('links each row to its conversation', async () => {
    start({ chats: [chat(C1, 'Minh')] });
    await flush();
    expect(rows()[0]!.getAttribute('href')).toBe(`/c/${C1}`);
  });

  it('always has links to friends and settings', async () => {
    start();
    await flush();
    expect(root.querySelector('header a[href="/friends"]')).not.toBeNull();
    expect(root.querySelector('header a[href="/settings"]')).not.toBeNull();
  });
});

describe('chat: opening a conversation', () => {
  const chats = [withLast(msg(3, 'minh'), { lastReadSeq: 1, unread: 2 }), chat(C2, 'Lan')];
  const history = { [C1]: [msg(1, 'mom'), msg(2, 'minh'), msg(3, 'minh')] };

  it('opens on a click, changes the address, and shows the messages oldest first', async () => {
    const h = start({ chats, history });
    await flush();

    rows()[0]!.click();
    await flush();

    expect(h.nav.push).toHaveBeenCalledWith(`/c/${C1}`);
    expect(h.api.history).toHaveBeenCalledWith(C1, undefined);
    expect(bubbles().map((b) => b.getAttribute('data-msg'))).toEqual(['id1', 'id2', 'id3']);
    expect(root.querySelector('.chat')!.getAttribute('data-pane')).toBe('thread');
    expect(text(root.querySelector('.th-head')!)).toContain('Minh');
  });

  it('does not take over a modified click (a new tab should open the link)', async () => {
    const h = start({ chats, history });
    await flush();
    rows()[0]!.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, ctrlKey: true }));
    await flush();
    expect(h.nav.push).not.toHaveBeenCalled();
  });

  it('opens straight from the address', async () => {
    const h = start({ path: `/c/${C1}`, chats, history });
    await flush();
    expect(h.api.history).toHaveBeenCalledWith(C1, undefined);
    expect(bubbles()).toHaveLength(3);
    expect(root.querySelector('.chat')!.getAttribute('data-pane')).toBe('thread');
  });

  it('says so when the address names a conversation you are not in', async () => {
    start({ path: `/c/${C2.replace('2', '3')}`, chats, history });
    await flush();
    expect(text(root.querySelector('.thread-pane')!)).toContain('Không tìm thấy cuộc trò chuyện');
  });

  it('goes back to the list with the back button, and follows the browser back button', async () => {
    const h = start({ chats, history });
    await flush();
    rows()[0]!.click();
    await flush();

    root.querySelector<HTMLElement>('.th-head .back')!.click();
    await flush();
    expect(h.nav.push).toHaveBeenLastCalledWith('/');
    expect(root.querySelector('.chat')!.getAttribute('data-pane')).toBe('list');

    h.nav.path = `/c/${C1}`; h.nav.pop();
    await flush();
    expect(root.querySelector('.chat')!.getAttribute('data-pane')).toBe('thread');
  });

  it('marks the active row', async () => {
    start({ path: `/c/${C1}`, chats, history });
    await flush();
    expect(rows()[0]!.getAttribute('aria-current')).toBe('page');
    expect(rows()[1]!.getAttribute('aria-current')).toBeNull();
  });

  it('puts the "new messages" divider before what you had not read', async () => {
    start({ path: `/c/${C1}`, chats, history });
    await flush();
    const divider = root.querySelector('.newdiv')!;
    expect(text(divider)).toBe('2 tin nhắn mới');
    expect(divider.nextElementSibling!.getAttribute('data-msg')).toBe('id2');
  });

  it('marks the conversation read once it is open and visible, and clears the badge', async () => {
    const h = start({ path: `/c/${C1}`, chats, history });
    await flush();
    expect(h.api.markRead).toHaveBeenCalledWith(C1, 3);
    expect(rows()[0]!.querySelector('.badge')).toBeNull();
  });

  it('does not mark it read while the page is hidden, and does when the page is shown again', async () => {
    const h = start({ path: `/c/${C1}`, chats, history });
    h.visible.value = false;
    await flush();
    expect(h.api.markRead).not.toHaveBeenCalled();
    expect(rows()[0]!.querySelector('.badge')!.textContent).toBe('2');

    h.visible.value = true;
    document.dispatchEvent(new Event('visibilitychange'));
    await flush();

    expect(h.api.markRead).toHaveBeenCalledWith(C1, 3);
    expect(rows()[0]!.querySelector('.badge')).toBeNull();
  });

  it('keeps the day pills and run grouping from the model', async () => {
    start({ path: `/c/${C1}`, chats, history });
    await flush();
    expect(text(root.querySelector('.day')!)).toBe('Hôm nay');
    const mine = root.querySelector('[data-msg="id1"]')!;
    expect(mine.classList.contains('out')).toBe(true);
    expect(root.querySelector('[data-msg="id2"]')!.classList.contains('in')).toBe(true);
  });

  it('shows the time on each message and keeps line breaks', async () => {
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh')], history: { [C1]: [msg(1, 'minh', 'dòng 1\ndòng 2')] } });
    await flush();
    const b = root.querySelector('[data-msg="id1"]')!;
    expect(b.querySelector('.txt')!.textContent).toBe('dòng 1\ndòng 2');
    expect(b.querySelector('.ts')!.textContent).toMatch(/\d{2}:\d{2}/);
  });

  it('never treats message text as HTML', async () => {
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh')], history: { [C1]: [msg(1, 'minh', '<img src=x onerror=alert(1)><b>bold</b>')] } });
    await flush();
    expect(root.querySelector('[data-msg="id1"] img')).toBeNull();
    expect(root.querySelector('[data-msg="id1"] b')).toBeNull();
    expect(root.querySelector('[data-msg="id1"] .txt')!.textContent).toContain('<img');
  });
});

describe('chat: older messages', () => {
  it('offers to load older ones only when there are more, and puts them before the rest', async () => {
    const newest = [msg(51, 'minh'), msg(52, 'minh')];
    const api = {
      history: vi.fn(async (_id: string, before?: number) => before === undefined
        ? ok({ messages: [...newest].reverse(), hasMore: true })
        : ok({ messages: [msg(50, 'minh'), msg(49, 'minh')], hasMore: false })),
    };
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh', { lastSeq: 52 })], api });
    await flush();

    const older = root.querySelector<HTMLButtonElement>('button[data-older]')!;
    older.click();
    await flush();

    expect(api.history).toHaveBeenLastCalledWith(C1, 51);
    expect(bubbles().map((b) => b.getAttribute('data-msg'))).toEqual(['id49', 'id50', 'id51', 'id52']);
    expect(root.querySelector('button[data-older]')).toBeNull();
  });

  it('shows no such button when everything is loaded', async () => {
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh')], history: { [C1]: [msg(1, 'minh')] } });
    await flush();
    expect(root.querySelector('button[data-older]')).toBeNull();
  });
});

describe('chat: live updates', () => {
  const chats = [withLast(msg(1, 'minh'), { lastReadSeq: 1 }), chat(C2, 'Lan')];

  it('puts a new message in the open thread, and keeps the badge at zero while you are looking', async () => {
    const h = start({ path: `/c/${C1}`, chats, history: { [C1]: [msg(1, 'minh')] } });
    await flush();

    h.handlers().event(C1, created(msg(2, 'minh', 'tin mới')));
    await flush();

    expect(bubbles().map((b) => b.getAttribute('data-msg'))).toEqual(['id1', 'id2']);
    expect(rows()[0]!.querySelector('.badge')).toBeNull();
    expect(text(rows()[0]!)).toContain('tin mới');
    expect(h.api.markRead).toHaveBeenLastCalledWith(C1, 2);
  });

  it('counts a message in a conversation that is not open, and moves it to the top', async () => {
    const h = start({ chats, history: {} });
    await flush();

    h.handlers().event(C2, created(msg(1, 'minh', 'xin chào', at(12, 5), C2)));
    await flush();

    expect(rows().map((r) => r.getAttribute('data-chat'))).toEqual([C2, C1]);
    expect(rows()[0]!.querySelector('.badge')!.textContent).toBe('1');
  });

  it('counts it as unread when the page is hidden, even for the open conversation', async () => {
    const h = start({ path: `/c/${C1}`, chats, history: { [C1]: [msg(1, 'minh')] } });
    await flush();
    h.visible.value = false;

    h.handlers().event(C1, created(msg(2, 'minh')));
    await flush();

    expect(rows()[0]!.querySelector('.badge')!.textContent).toBe('1');
    expect(h.api.markRead).not.toHaveBeenCalledWith(C1, 2);
  });

  it('does not count your own message from another device as unread', async () => {
    const h = start({ chats, history: {} });
    await flush();
    h.handlers().event(C1, created(msg(2, 'mom', 'từ điện thoại')));
    await flush();
    expect(rows()[0]!.querySelector('.badge')).toBeNull();
    expect(text(rows()[0]!)).toContain('Bạn: từ điện thoại');
  });

  it('shows only the start of a long message in the list, though the thread has all of it', async () => {
    const long = 'x'.repeat(1000);
    const h = start({ path: `/c/${C1}`, chats, history: { [C1]: [msg(1, 'minh')] } });
    await flush();
    h.handlers().event(C1, created(msg(2, 'minh', long)));
    await flush();
    expect(rows()[0]!.querySelector('.pv')!.textContent).toHaveLength(200);
    expect(root.querySelector('[data-msg="id2"] .txt')!.textContent).toHaveLength(1000);
  });

  it('counts a repeated event only once', async () => {
    const h = start({ chats, history: {} });
    await flush();
    h.handlers().event(C2, created(msg(1, 'minh', 'x', at(12), C2)));
    h.handlers().event(C2, created(msg(1, 'minh', 'x', at(12), C2)));
    await flush();
    expect(rows()[0]!.querySelector('.badge')!.textContent).toBe('1');
  });

  it('asks the server for what it missed when a gap shows', async () => {
    const h = start({ path: `/c/${C1}`, chats, history: { [C1]: [msg(1, 'minh')] } });
    await flush();

    h.handlers().event(C1, created(msg(4, 'minh')));
    await flush();

    expect(h.live.sync).toHaveBeenCalledWith(C1, 1);
  });

  it('asks even when only one message is missing', async () => {
    const h = start({ path: `/c/${C1}`, chats, history: { [C1]: [msg(1, 'minh')] } });
    await flush();
    h.handlers().event(C1, created(msg(3, 'minh')));
    await flush();
    expect(h.live.sync).toHaveBeenCalledWith(C1, 1);
  });

  it('does not ask when the next message follows on', async () => {
    const h = start({ path: `/c/${C1}`, chats, history: { [C1]: [msg(1, 'minh')] } });
    await flush();
    h.handlers().event(C1, created(msg(2, 'minh')));
    await flush();
    expect(h.live.sync).not.toHaveBeenCalled();
  });

  it('takes in a sync page, and asks for the next page while there is more', async () => {
    const h = start({ path: `/c/${C1}`, chats, history: { [C1]: [msg(1, 'minh')] } });
    await flush();

    h.handlers().events(C1, [created(msg(2, 'minh')), created(msg(3, 'minh'))], true);
    await flush();

    expect(bubbles()).toHaveLength(3);
    expect(h.live.sync).toHaveBeenCalledWith(C1, 3);
    h.live.sync.mockClear();
    h.handlers().events(C1, [created(msg(4, 'minh'))], false);
    await flush();
    expect(h.live.sync).not.toHaveBeenCalled();
  });

  it('reloads the list when a message arrives for a conversation it has never heard of', async () => {
    const h = start({ chats, history: {} });
    await flush();
    const before = (h.api.conversations as ReturnType<typeof vi.fn>).mock.calls.length;

    h.handlers().event('99999999-9999-4999-8999-999999999999', created(msg(1, 'minh', 'ai đó', at(12), '99999999-9999-4999-8999-999999999999')));
    await flush();

    expect((h.api.conversations as ReturnType<typeof vi.fn>).mock.calls.length).toBe(before + 1);
  });

  it('after the connection comes back, reloads the list and catches the open thread up', async () => {
    const h = start({ path: `/c/${C1}`, chats, history: { [C1]: [msg(1, 'minh')] } });
    await flush();
    const before = (h.api.conversations as ReturnType<typeof vi.fn>).mock.calls.length;

    h.handlers().state(false);
    h.handlers().state(true);
    await flush();

    expect((h.api.conversations as ReturnType<typeof vi.fn>).mock.calls.length).toBe(before + 1);
    expect(h.live.sync).toHaveBeenCalledWith(C1, 1);
  });

  it('goes to sign-in when the session ends', async () => {
    const h = start();
    await flush();
    h.handlers().ended();
    expect(h.go).toHaveBeenCalledWith('/sign-in');
  });

  it('says so when another window took this one\'s place', async () => {
    const h = start();
    await flush();
    h.handlers().replaced();
    await flush();
    expect(text(root.querySelector('[role="status"]')!)).toContain('mở ở nơi khác');
  });

  it('tries the connection again right away when the network comes back', async () => {
    const h = start();
    await flush();
    dispatchEvent(new Event('online'));
    expect(h.live.retryNow).toHaveBeenCalled();
  });

  it('tries the connection again right away when the page comes back into view', async () => {
    const h = start();
    await flush();
    document.dispatchEvent(new Event('visibilitychange'));
    expect(h.live.retryNow).toHaveBeenCalled();
  });
});

describe('chat: writing', () => {
  const chats = [chat(C1, 'Minh')];
  const open = (extra: Record<string, unknown> = {}) => start({ path: `/c/${C1}`, chats, history: { [C1]: [msg(1, 'minh')] }, api: extra });

  it('has a labelled message field and a send button', async () => {
    open();
    await flush();
    const field = root.querySelector<HTMLTextAreaElement>('textarea[name="message"]')!;
    expect(field.getAttribute('aria-label')).toBe('Nhập tin nhắn');
    expect(field.getAttribute('placeholder')).toBe('Nhập tin nhắn');
    expect(root.querySelector('button.send')!.getAttribute('aria-label')).toBe('Gửi');
  });

  it('sends what you typed, shows it at once as being sent, then as sent', async () => {
    let finish: (r: ReturnType<typeof ok>) => void = () => undefined;
    const send = vi.fn(() => new Promise((resolve) => { finish = resolve as never; }));
    const h = open({ send });
    await flush();

    compose('  xin chào  ');
    submitForm();
    await flush();

    expect(send).toHaveBeenCalledWith(C1, 'new-1', 'xin chào');
    const pending = root.querySelector('[data-msg="new-1"]')!;
    expect(pending.textContent).toContain('xin chào');
    expect(pending.getAttribute('data-state')).toBe('sending');
    expect(root.querySelector<HTMLTextAreaElement>('textarea')!.value).toBe('');

    finish(ok({ ...msg(2, 'mom', 'xin chào'), clientId: 'new-1' }));
    await flush();

    expect(root.querySelector('[data-msg="new-1"]')).toBeNull();
    expect(root.querySelector('[data-msg="id2"]')!.textContent).toContain('xin chào');
    expect(text(rows()[0]!)).toContain('Bạn: xin chào');
    expect(rows()[0]!.querySelector('.badge')).toBeNull();
    expect(h.live.sync).not.toHaveBeenCalled();
  });

  it('does not send an empty message', async () => {
    const send = vi.fn();
    open({ send });
    await flush();
    compose('   \n ');
    submitForm();
    await flush();
    expect(send).not.toHaveBeenCalled();
  });

  it('shows one copy when the live event for your own message arrives as well as the answer', async () => {
    const mine = { ...msg(2, 'mom', 'một lần'), clientId: 'new-1' };
    const h = open({ send: vi.fn(async () => ok(mine)) });
    await flush();
    compose('một lần');
    submitForm();
    h.handlers().event(C1, created(mine));
    await flush();
    expect(bubbles().filter((b) => b.textContent!.includes('một lần'))).toHaveLength(1);
  });

  it('shows a copy that arrives live first as the real message, not a second one', async () => {
    let finish: (r: unknown) => void = () => undefined;
    const mine = { ...msg(2, 'mom', 'đến trước'), clientId: 'new-1' };
    const h = open({ send: vi.fn(() => new Promise((resolve) => { finish = resolve; })) });
    await flush();
    compose('đến trước');
    submitForm();
    await flush();

    h.handlers().event(C1, created(mine));
    await flush();
    expect(bubbles().filter((b) => b.textContent!.includes('đến trước'))).toHaveLength(1);
    finish(ok(mine));
    await flush();
    expect(bubbles().filter((b) => b.textContent!.includes('đến trước'))).toHaveLength(1);
  });

  it('marks a refused message as not sent, with the reason in words and two ways forward', async () => {
    open({ send: vi.fn(async () => fail('not_friends', 403)) });
    await flush();
    compose('không gửi được');
    submitForm();
    await flush();

    const failed = root.querySelector('[data-msg="new-1"]')!;
    expect(failed.getAttribute('data-state')).toBe('failed');
    expect(text(failed.parentElement!)).toContain('Chỉ nhắn tin được với bạn bè');
    expect(root.querySelector('button[data-retry]')).not.toBeNull();
    expect(root.querySelector('button[data-discard]')).not.toBeNull();
  });

  it('"Gửi lại" sends the same message with the same id, so it cannot arrive twice', async () => {
    const send = vi.fn()
      .mockResolvedValueOnce(fail('invalid_message', 400))
      .mockResolvedValueOnce(ok({ ...msg(2, 'mom', 'thử lại'), clientId: 'new-1' }));
    open({ send });
    await flush();
    compose('thử lại');
    submitForm();
    await flush();

    root.querySelector<HTMLButtonElement>('button[data-retry]')!.click();
    await flush();

    expect(send.mock.calls.map((c) => [c[1], c[2]])).toEqual([['new-1', 'thử lại'], ['new-1', 'thử lại']]);
    expect(root.querySelector('button[data-retry]')).toBeNull();
    expect(root.querySelector('[data-msg="id2"]')).not.toBeNull();
  });

  it('"Xóa" throws the unsent message away', async () => {
    open({ send: vi.fn(async () => fail('invalid_message', 400)) });
    await flush();
    compose('bỏ đi');
    submitForm();
    await flush();

    root.querySelector<HTMLButtonElement>('button[data-discard]')!.click();
    await flush();

    expect(root.querySelector('[data-msg="new-1"]')).toBeNull();
  });

  it('keeps what you were typing in each conversation when you move between them', async () => {
    const h = start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh'), chat(C2, 'Lan')], history: { [C1]: [msg(1, 'minh')], [C2]: [msg(1, 'minh', 'x', at(10), C2)] } });
    await flush();
    compose('dở dang');

    h.nav.path = `/c/${C2}`; h.nav.pop();
    await flush();
    expect(root.querySelector<HTMLTextAreaElement>('textarea')!.value).toBe('');
    h.nav.path = `/c/${C1}`; h.nav.pop();
    await flush();
    expect(root.querySelector<HTMLTextAreaElement>('textarea')!.value).toBe('dở dang');
  });
});

describe('chat: the Enter key', () => {
  const setup = async () => {
    const send = vi.fn(async () => ok({ ...msg(2, 'mom', 'x'), clientId: 'new-1' }));
    const h = start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh')], history: { [C1]: [msg(1, 'minh')] }, api: { send } });
    await flush();
    return { send, h, field: root.querySelector<HTMLTextAreaElement>('textarea')! };
  };
  const press = (el: HTMLElement, init: KeyboardEventInit) => {
    const e = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true, ...init });
    el.dispatchEvent(e);
    return e;
  };

  it('sends on Enter with a keyboard, and Shift+Enter makes a new line', async () => {
    const { send, field } = await setup();
    compose('gửi đi');
    expect(press(field, {}).defaultPrevented).toBe(true);
    await flush();
    expect(send).toHaveBeenCalledOnce();

    compose('dòng một');
    expect(press(field, { shiftKey: true }).defaultPrevented).toBe(false);
    await flush();
    expect(send).toHaveBeenCalledOnce();
  });

  it('does not send while a word is being composed (Vietnamese Telex and VNI use this)', async () => {
    const { send, field } = await setup();
    compose('vie^t');
    expect(press(field, { isComposing: true }).defaultPrevented).toBe(false);
    await flush();
    expect(send).not.toHaveBeenCalled();
  });

  it('leaves Enter as a new line on a phone, where the send button is for sending', async () => {
    const send = vi.fn();
    const h = start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh')], history: { [C1]: [msg(1, 'minh')] }, api: { send } });
    h.coarse.value = true;
    await flush();
    compose('hai dòng');
    expect(press(root.querySelector('textarea')!, {}).defaultPrevented).toBe(false);
    await flush();
    expect(send).not.toHaveBeenCalled();
  });
});

describe('chat: scrolling', () => {
  function stubScroll(box: HTMLElement, o: { height: number; top: number; client: number }) {
    Object.defineProperty(box, 'scrollHeight', { value: o.height, configurable: true });
    Object.defineProperty(box, 'clientHeight', { value: o.client, configurable: true });
    box.scrollTop = o.top;
  }

  it('stays at the bottom for a new message when you were at the bottom', async () => {
    const h = start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh', { lastReadSeq: 1 })], history: { [C1]: [msg(1, 'minh')] } });
    await flush();
    const box = root.querySelector<HTMLElement>('.msgs')!;
    stubScroll(box, { height: 1000, top: 900, client: 100 });
    box.dispatchEvent(new Event('scroll'));

    h.handlers().event(C1, created(msg(2, 'minh')));
    await flush();

    expect(box.scrollTop).toBe(1000);
  });

  it('does not pull you down when you have scrolled up to read', async () => {
    const h = start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh', { lastReadSeq: 1 })], history: { [C1]: [msg(1, 'minh')] } });
    await flush();
    const box = root.querySelector<HTMLElement>('.msgs')!;
    stubScroll(box, { height: 1000, top: 200, client: 100 });
    box.dispatchEvent(new Event('scroll'));

    h.handlers().event(C1, created(msg(2, 'minh')));
    await flush();

    expect(box.scrollTop).toBe(200);
  });
});


describe('chat: the connection banner', () => {
  const chats = [chat(C1, 'Minh')];

  it('shows nothing while connected, a warning when the connection is lost, and nothing again when it returns', async () => {
    const h = start({ path: `/c/${C1}`, chats, history: { [C1]: [msg(1, 'minh')] } });
    await flush();
    expect(root.querySelector('.warn')).toBeNull();

    h.handlers().state(false);
    await flush();
    expect(root.querySelector('.thread-pane .warn')).not.toBeNull();   // a phone shows only this pane
    expect(text(root.querySelector('.warn')!)).toContain('Mất kết nối');
    expect(root.querySelector('.warn')!.getAttribute('role')).toBe('status');

    h.handlers().state(true);
    await flush();
    expect(root.querySelector('.warn')).toBeNull();
  });

  it('shows on the list as well, so a phone showing only the list still knows', async () => {
    const h = start({ chats });
    await flush();
    h.handlers().state(false);
    await flush();
    expect(root.querySelector('.list-pane .warn')).not.toBeNull();
  });
});

describe('chat: sending while the connection is bad', () => {
  const chats = [chat(C1, 'Minh')];
  const open = (extra: Record<string, unknown> = {}, outbox: Harness['outbox']['items'] = []) =>
    start({ path: `/c/${C1}`, chats, history: { [C1]: [msg(1, 'minh')] }, api: extra, outbox });
  const pendingOf = (id: string) => root.querySelector(`[data-msg="${id}"]`);
  const write = (body: string) => { compose(body); submitForm(); };
  const networkDown = () => fail('network', 0);

  it('keeps a message that could not be sent and waits, with a clock and a note, instead of calling it failed', async () => {
    open({ send: vi.fn(async () => networkDown()) });
    await flush();

    write('đi chợ');
    await flush();

    const queued = pendingOf('new-1')!;
    expect(queued.getAttribute('data-state')).toBe('queued');
    expect(queued.querySelector('.ts .i')).not.toBeNull();
    expect(text(queued.parentElement!)).toContain('Sẽ gửi khi có mạng');
    expect(root.querySelector('button[data-retry]')).toBeNull();
  });

  it('says "will try again" rather than "when there is a network" when the server, not the network, was the problem', async () => {
    open({ send: vi.fn(async () => fail('server', 503)) });
    await flush();
    write('máy chủ');
    await flush();
    expect(text(pendingOf('new-1')!.parentElement!)).toContain('Sẽ thử gửi lại');
    expect(text(pendingOf('new-1')!.parentElement!)).not.toContain('có mạng');
  });

  it('sends it again by itself when the live connection returns, and then it is an ordinary message', async () => {
    const send = vi.fn()
      .mockResolvedValueOnce(networkDown())
      .mockResolvedValueOnce(ok({ ...msg(2, 'mom', 'đi chợ'), clientId: 'new-1' }));
    const h = open({ send });
    await flush();
    write('đi chợ');
    await flush();

    h.handlers().state(false);
    h.handlers().state(true);
    await flush();

    expect(send.mock.calls.map((c) => c[1])).toEqual(['new-1', 'new-1']);
    expect(pendingOf('new-1')).toBeNull();
    expect(pendingOf('id2')).not.toBeNull();
  });

  it('sends it again when the browser says the network is back', async () => {
    const send = vi.fn().mockResolvedValueOnce(networkDown()).mockResolvedValueOnce(ok({ ...msg(2, 'mom', 'x'), clientId: 'new-1' }));
    open({ send });
    await flush();
    write('x');
    await flush();

    dispatchEvent(new Event('online'));
    await flush();

    expect(send).toHaveBeenCalledTimes(2);
    expect(pendingOf('id2')).not.toBeNull();
  });

  it('tries again on a timer too, waiting 1 s, 2 s, 4 s ... and never more than 30 s', async () => {
    const send = vi.fn(async () => networkDown());
    const h = open({ send });
    await flush();
    write('chờ');
    await flush();

    const waits: number[] = [];
    for (let i = 0; i < 8; i++) {
      const timer = h.timers.filter((t) => t.live).at(-1)!;
      waits.push(timer.ms);
      timer.fn();
      await flush();
    }

    expect(waits).toEqual([1000, 2000, 4000, 8000, 16000, 30000, 30000, 30000]);
    expect(send).toHaveBeenCalledTimes(9);
  });

  it('stops the timer once the message has gone', async () => {
    const send = vi.fn().mockResolvedValueOnce(networkDown()).mockResolvedValueOnce(ok({ ...msg(2, 'mom', 'x'), clientId: 'new-1' }));
    const h = open({ send });
    await flush();
    write('x');
    await flush();
    h.handlers().state(true);
    await flush();
    const callsBefore = send.mock.calls.length;

    for (const t of h.timers) t.fn();
    await flush();

    expect(send.mock.calls.length).toBe(callsBefore);
  });

  it('keeps messages in the order they were written: the second waits for the first', async () => {
    const order: string[] = [];
    let n = 0;
    const send = vi.fn(async (_c: string, clientId: string, body: string) => {
      order.push(`${clientId}:${body}`);
      if (clientId === 'new-1' && n++ === 0) return networkDown();
      return ok({ ...msg(order.length + 1, 'mom', body), clientId });
    });
    const h = open({ send });
    await flush();

    write('một');
    await flush();
    write('hai');
    await flush();
    expect(order).toEqual(['new-1:một']);                 // the second was not tried while the first waits

    h.handlers().state(true);
    await flush();
    expect(order).toEqual(['new-1:một', 'new-1:một', 'new-2:hai']);
    expect(pendingOf('new-2')).toBeNull();
  });

  it('retries a busy server a few times and then gives up with the usual two choices', async () => {
    const send = vi.fn(async () => fail('server', 503));
    const h = open({ send });
    await flush();
    write('máy chủ bận');
    await flush();
    for (let i = 0; i < 6; i++) {
      h.timers.filter((t) => t.live).at(-1)?.fn();
      await flush();
    }

    expect(send.mock.calls.length).toBe(5);
    expect(pendingOf('new-1')!.getAttribute('data-state')).toBe('failed');
    expect(root.querySelector('button[data-retry]')).not.toBeNull();
    expect(root.querySelector('button[data-discard]')).not.toBeNull();
  });

  it('waits as long as the server asks when told to slow down', async () => {
    const send = vi.fn(async () => fail('too_many_messages', 429, 7));
    const h = open({ send });
    await flush();
    write('nhanh quá');
    await flush();

    expect(pendingOf('new-1')!.getAttribute('data-state')).toBe('queued');
    expect(h.timers.filter((t) => t.live).at(-1)!.ms).toBe(7000);
  });

  it('never waits less than a second, even if told zero', async () => {
    const h = open({ send: vi.fn(async () => fail('too_many_messages', 429, 0)) });
    await flush();
    write('ngay');
    await flush();
    expect(h.timers.filter((t) => t.live).at(-1)!.ms).toBe(1000);
  });

  it('keeps only one retry timer going even when two things are waiting', async () => {
    const send = vi.fn(async (_c: string, clientId: string) => (clientId === 'new-1' ? fail('invalid_message', 400) : fail('network', 0)));
    const h = open({ send });
    await flush();
    write('bị từ chối');
    await flush();
    write('chờ mạng');
    await flush();
    expect(h.timers.filter((t) => t.live)).toHaveLength(1);

    root.querySelector<HTMLButtonElement>('button[data-retry]')!.click();
    await flush();

    expect(h.timers.filter((t) => t.live)).toHaveLength(1);
  });

  it('starts the waits from one second again after something has gone through', async () => {
    const results = [fail('network', 0), fail('network', 0)];
    const send = vi.fn(async (_c: string, clientId: string) => results.shift() ?? ok({ ...msg(9, 'mom', 'x'), clientId, id: `ok-${clientId}` }));
    const h = open({ send });
    await flush();
    write('một');
    await flush();
    h.timers.filter((t) => t.live).at(-1)!.fn();   // second failure: the wait grows to 2 s
    await flush();
    expect(h.timers.filter((t) => t.live).at(-1)!.ms).toBe(2000);
    h.timers.filter((t) => t.live).at(-1)!.fn();   // goes through
    await flush();

    send.mockResolvedValueOnce(fail('network', 0));
    write('hai');
    await flush();
    expect(h.timers.filter((t) => t.live).at(-1)!.ms).toBe(1000);
  });

  it('a message the server refuses does not hold back the ones after it', async () => {
    const send = vi.fn(async (_c: string, clientId: string) =>
      clientId === 'new-1' ? fail('not_friends', 403) : ok({ ...msg(2, 'mom', 'tiếp'), clientId }));
    open({ send });
    await flush();
    write('bị từ chối');
    await flush();
    write('tiếp');
    await flush();

    expect(pendingOf('new-1')!.getAttribute('data-state')).toBe('failed');
    expect(pendingOf('id2')).not.toBeNull();
  });

  it('"Gửi lại" on a failed message puts it back in line and sends it', async () => {
    const send = vi.fn()
      .mockResolvedValueOnce(fail('empty_message', 400))
      .mockResolvedValueOnce(ok({ ...msg(2, 'mom', 'lại'), clientId: 'new-1' }));
    open({ send });
    await flush();
    write('lại');
    await flush();
    root.querySelector<HTMLButtonElement>('button[data-retry]')!.click();
    await flush();
    expect(pendingOf('id2')).not.toBeNull();
  });

  it('goes to sign-in when the session has gone, and keeps the message', async () => {
    const h = open({ send: vi.fn(async () => fail('unauthorized', 401)) });
    await flush();
    write('hết phiên');
    await flush();
    expect(h.go).toHaveBeenCalledWith('/sign-in');
    expect(h.outbox.items.map((i) => i.body)).toEqual(['hết phiên']);
  });
});

describe('chat: the outbox survives a reload', () => {
  const chats = [chat(C1, 'Minh')];
  const open = (extra: Record<string, unknown> = {}, outbox: Harness['outbox']['items'] = []) =>
    start({ path: `/c/${C1}`, chats, history: { [C1]: [msg(1, 'minh')] }, api: extra, outbox });
  const write = (body: string) => { compose(body); submitForm(); };

  it('writes a message that could not be sent to the outbox, and takes it out once it has gone', async () => {
    const send = vi.fn().mockResolvedValueOnce(fail('network', 0)).mockResolvedValueOnce(ok({ ...msg(2, 'mom', 'giữ lại'), clientId: 'new-1' }));
    const h = open({ send });
    await flush();

    write('giữ lại');
    await flush();
    expect(h.outbox.items).toEqual([{ conversation: C1, clientId: 'new-1', body: 'giữ lại', createdAt: NOW.toISOString() }]);

    h.handlers().state(true);
    await flush();
    expect(h.outbox.items).toEqual([]);
  });

  it('also saves a message the moment it is written, in case the page is closed before the answer', async () => {
    let finish: (r: unknown) => void = () => undefined;
    const h = open({ send: vi.fn(() => new Promise((resolve) => { finish = resolve; })) });
    await flush();
    write('đang bay');
    await flush();
    expect(h.outbox.items.map((i) => i.body)).toEqual(['đang bay']);
    finish(ok({ ...msg(2, 'mom', 'đang bay'), clientId: 'new-1' }));
    await flush();
    expect(h.outbox.items).toEqual([]);
  });

  it('does not keep a message the server refused, and does not keep one that was thrown away', async () => {
    const h = open({ send: vi.fn(async () => fail('not_friends', 403)) });
    await flush();
    write('bị từ chối');
    await flush();
    expect(h.outbox.items).toEqual([]);
  });

  it('sends what was left over from before the page was closed, as the same message', async () => {
    const send = vi.fn(async (_c: string, clientId: string, body: string) => ok({ ...msg(2, 'mom', body), clientId }));
    const h = open({ send }, [{ conversation: C1, clientId: 'old-1', body: 'từ lần trước', createdAt: at(11) }]);
    await flush();

    expect(send).toHaveBeenCalledWith(C1, 'old-1', 'từ lần trước');
    expect(root.querySelector('[data-msg="id2"]')).not.toBeNull();
    expect(h.outbox.items).toEqual([]);
  });

  it('shows it as waiting until it has gone, and in the right place when sent', async () => {
    const send = vi.fn(async () => fail('network', 0));
    open({ send }, [{ conversation: C1, clientId: 'old-1', body: 'chờ nhé', createdAt: at(11) }]);
    await flush();
    const waiting = root.querySelector('[data-msg="old-1"]')!;
    expect(waiting.getAttribute('data-state')).toBe('queued');
    expect(waiting.textContent).toContain('chờ nhé');
  });

  it('drops a leftover for a conversation you are no longer in', async () => {
    const send = vi.fn();
    const h = open({ send }, [{ conversation: C2, clientId: 'old-2', body: 'đi lạc', createdAt: at(11) }]);
    await flush();
    expect(send).not.toHaveBeenCalled();
    expect(h.outbox.items).toEqual([]);
  });
});
