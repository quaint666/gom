import { beforeEach, describe, expect, it, vi } from 'vitest';
import { avatarTone } from '../Scripts/chat/model.js';
import type { Attachment, ChatEvent, ChatMessage, Conversation } from '../Scripts/api.js';
import type { LiveControl, LiveHandlers } from '../Scripts/chat/live.js';
import { mountChat, type ChatDeps, type OutboxItem } from '../Scripts/pages/chat.js';
import { fail, fakeApi, fill, flush, ok, text } from './helpers.js';

let root: HTMLElement;
beforeEach(() => { root = document.createElement('div'); document.body.replaceChildren(root); });

const me = { id: 'u-mom', gomId: 'mom', displayName: 'Mẹ', isOwner: false };
const C1 = '11111111-1111-4111-8111-111111111111';
const C2 = '22222222-2222-4222-8222-222222222222';
const NOW = new Date(Date.UTC(2026, 11, 10, 12, 30));
const at = (hh: number, mm = 0) => new Date(Date.UTC(2026, 11, 10, hh, mm)).toISOString();

const msg = (seq: number, from: 'mom' | 'minh', body = `m${seq}`, when = at(10, seq), conv = C1): ChatMessage => ({
  id: `id${seq}`, seq, conversationId: conv, clientId: `cl${seq}`, sender: from === 'mom' ? { id: 'u-mom', displayName: 'Mẹ' } : { id: 'u-minh', displayName: 'Minh' }, body, createdAt: when,
});
const created = (m: ChatMessage): ChatEvent => ({ seq: m.seq, kind: 'created', message: m });
const recalled = (m: ChatMessage, eventSeq: number): ChatEvent => ({ seq: eventSeq, kind: 'deleted', message: { ...m, body: '', deleted: true } });

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
  outbox: { items: OutboxItem[]; saves: number };
}

function start(opts: { lastSeq?: Record<string, number>; outbox?: Harness['outbox']['items']; path?: string; chats?: Conversation[]; history?: Record<string, ChatMessage[]>; hasMore?: boolean; api?: Record<string, unknown> } = {}): Harness {
  const history = opts.history ?? {};
  const api = fakeApi({
    me: vi.fn(async () => ok(me)),
    // A fresh copy each time, as a real server gives: the page changes what it is handed.
    conversations: vi.fn(async () => ok(structuredClone(opts.chats ?? []))),
    history: vi.fn(async (id: string) => ok({ messages: [...(history[id] ?? [])].reverse(), hasMore: opts.hasMore ?? false, lastSeq: opts.lastSeq?.[id] ?? Math.max(0, ...(history[id] ?? []).map((m) => m.seq)) })),
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

  it('lets a message from someone else rise in once, and not the ones already there', async () => {
    vi.useFakeTimers();
    try {
      const h = start({ path: `/c/${C1}`, chats, history: { [C1]: [msg(1, 'minh')] } });
      await vi.advanceTimersByTimeAsync(50);
      h.handlers().event(C1, created(msg(2, 'minh', 'tin mới')));
      await vi.advanceTimersByTimeAsync(10);
      expect(bubbles().map((b) => b.closest('.mrow')!.classList.contains('fresh'))).toEqual([false, true]);
      await vi.advanceTimersByTimeAsync(700);
      h.handlers().event(C1, created(msg(3, 'minh', 'nữa')));
      await vi.advanceTimersByTimeAsync(10);
      expect(bubbles().map((b) => b.closest('.mrow')!.classList.contains('fresh'))).toEqual([false, false, true]);
    } finally { vi.useRealTimers(); }
  });

  it('says "Đã gửi một hình ảnh" in the list when the last message is only a picture, and for a file too', async () => {
    const sender = { id: 'u-minh', displayName: 'Minh' };
    const pic = chat(C1, 'Minh', { lastSeq: 1, lastMessage: { seq: 1, body: '', createdAt: at(10), sender, attachment: 'image' } });
    const doc = chat(C2, 'Lan', { lastSeq: 1, lastMessage: { seq: 1, body: '', createdAt: at(10), sender: { id: 'u-lan', displayName: 'Lan' }, attachment: 'file' } });
    start({ chats: [pic, doc] });
    await flush();
    expect(text(rows()[0]!.querySelector('.pv')!)).toBe('Đã gửi một hình ảnh');
    expect(text(rows()[1]!.querySelector('.pv')!)).toBe('Đã gửi một tệp');
  });

  it('takes the kind of the attachment for the list preview from a live message', async () => {
    const h = start({ chats: [chat(C2, 'Lan')], history: {} });
    await flush();
    h.handlers().event(C2, created({ ...msg(1, 'minh', '', at(12, 5), C2), attachments: [{ id: 'a1', kind: 'image', name: 'x.webp', size: 10, width: 4, height: 3 }] }));
    await flush();
    expect(text(rows()[0]!.querySelector('.pv')!)).toBe('Đã gửi một hình ảnh');
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

    expect(send).toHaveBeenCalledWith(C1, 'new-1', 'xin chào', undefined);
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

    expect(send).toHaveBeenCalledWith(C1, 'old-1', 'từ lần trước', undefined);
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


describe('chat: groups', () => {
  const G1 = '33333333-3333-4333-8333-333333333333';
  const group = (extra: Partial<Conversation> = {}): Conversation =>
    ({ id: G1, kind: 'group', title: 'Gia đình', peer: null, lastSeq: 0, lastReadSeq: 0, unread: 0, lastMessage: null, memberCount: 4, ...extra });
  const gmsg = (seq: number, from: 'mom' | 'minh' | 'ba', body = `g${seq}`, when = at(10, seq)): ChatMessage => ({
    id: `g${seq}`, seq, conversationId: G1, clientId: `gc${seq}`, body, createdAt: when,
    sender: from === 'mom' ? { id: 'u-mom', displayName: 'Mẹ' } : from === 'minh' ? { id: 'u-minh', displayName: 'Minh' } : { id: 'u-ba', displayName: 'Chị Ba' },
  });

  it('shows the group name, its size and a link to its members', async () => {
    start({ path: `/c/${G1}`, chats: [group()], history: { [G1]: [gmsg(1, 'minh')] } });
    await flush();
    const head = root.querySelector('.th-head')!;
    expect(text(head)).toContain('Gia đình');
    expect(text(head)).toContain('4 thành viên');
    expect(head.querySelector(`a[href="/groups/${G1}"]`)).not.toBeNull();
    expect(text(head)).not.toContain('@');
  });

  it('names who is speaking: the name before a run, the initials beside its last message, nothing for you', async () => {
    start({ path: `/c/${G1}`, chats: [group()], history: { [G1]: [gmsg(1, 'minh'), gmsg(2, 'minh'), gmsg(3, 'mom'), gmsg(4, 'ba')] } });
    await flush();
    const row = (id: string) => root.querySelector(`[data-msg="${id}"]`)!;
    expect(text(row('g1').querySelector('.sender')!)).toBe('Minh');
    expect(row('g2').querySelector('.sender')).toBeNull();               // the same run
    expect(row('g1').querySelector('.av')).toBeNull();                   // not the last of the run
    expect(row('g2').querySelector('.av')!.textContent).toBe('M');
    expect(row('g3').querySelector('.sender')).toBeNull();               // you
    expect(row('g3').querySelector('.av')).toBeNull();
    expect(text(row('g4').querySelector('.sender')!)).toBe('Chị Ba');
    expect(row('g4').querySelector('.av')!.textContent).toBe('CB');
  });

  it('shows no names or initials in a one-to-one conversation', async () => {
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh')], history: { [C1]: [msg(1, 'minh')] } });
    await flush();
    expect(root.querySelector('[data-msg="id1"] .sender')).toBeNull();
    expect(root.querySelector('[data-msg="id1"] .av')).toBeNull();
  });

  it('puts the speaker in front of a group\'s last message in the list', async () => {
    start({ chats: [group({ lastSeq: 1, lastMessage: { seq: 1, body: 'tối nay ăn gì', createdAt: at(11), sender: { id: 'u-minh', displayName: 'Minh' } } })] });
    await flush();
    expect(text(rows()[0]!.querySelector('.pv')!)).toBe('Minh: tối nay ăn gì');
    expect(rows()[0]!.querySelector('.av')!.textContent).toBe('GĐ');
  });

  it('reloads the list when told it changed', async () => {
    const h = start({ chats: [group()] });
    await flush();
    const before = (h.api.conversations as ReturnType<typeof vi.fn>).mock.calls.length;
    h.handlers().conversations();
    await flush();
    expect((h.api.conversations as ReturnType<typeof vi.fn>).mock.calls.length).toBe(before + 1);
  });

  it('has one way into friends and groups (the contacts page) in the header, and settings stays where it was', async () => {
    start({ chats: [] });
    await flush();
    expect(root.querySelector('header a[href="/friends"]')).not.toBeNull();
    expect(root.querySelector('header a[href="/settings"]')).not.toBeNull();
    expect(root.querySelector('header a[href="/groups/new"]')).toBeNull();
  });

  it('says "you are no longer in this group" when the open group disappears from the list', async () => {
    const h = start({ path: `/c/${G1}`, chats: [group()], history: { [G1]: [gmsg(1, 'minh')] } });
    await flush();
    (h.api.conversations as ReturnType<typeof vi.fn>).mockResolvedValueOnce(ok([]));
    h.handlers().conversations();
    await flush();
    expect(text(root.querySelector('.thread-pane')!)).toContain('Không tìm thấy cuộc trò chuyện');
  });
});


describe('chat: taking a message back', () => {
  const mine = msg(1, 'mom', 'lỡ gửi nhầm');
  const theirs = msg(2, 'minh', 'tin của Minh');
  const chats = [withLast(theirs, { lastReadSeq: 2 })];
  const open = (extra: Record<string, unknown> = {}, history = [mine, theirs], more: Record<string, unknown> = {}) =>
    start({ path: `/c/${C1}`, chats, history: { [C1]: history }, api: extra, ...more });
  const row = (id: string) => root.querySelector(`[data-msg="${id}"]`)!;

  it('shows a recalled message as a placeholder with none of its words, in your voice and theirs', async () => {
    open({}, [{ ...msg(1, 'mom', ''), deleted: true }, { ...msg(2, 'minh', ''), deleted: true }]);
    await flush();
    expect(row('id1').classList.contains('recalled')).toBe(true);
    expect(text(row('id1'))).toContain('Bạn đã thu hồi một tin nhắn');
    expect(text(row('id2'))).toContain('Tin nhắn đã được thu hồi');
    expect(row('id1').querySelector('.txt:not(.gone)')).toBeNull();
  });

  it('names who took it back in a group', async () => {
    const G = '33333333-3333-4333-8333-333333333333';
    const ghost: ChatMessage = { ...msg(1, 'minh', '', at(10), G), deleted: true };
    start({ path: `/c/${G}`, chats: [{ id: G, kind: 'group', title: 'Nhóm', peer: null, lastSeq: 1, lastReadSeq: 1, unread: 0, lastMessage: null, memberCount: 3 }], history: { [G]: [ghost] } });
    await flush();
    expect(text(root.querySelector('[data-msg="id1"]')!)).toContain('Minh đã thu hồi một tin nhắn');
  });

  it('offers the menu on every message that has arrived, and "Thu hồi" only on your own', async () => {
    open({ send: vi.fn(async () => fail('network', 0)) });
    await flush();
    expect(row('id1').querySelector('button[data-menu]')).not.toBeNull();
    expect(row('id2').querySelector('button[data-menu]')).not.toBeNull();
    row('id1').querySelector<HTMLButtonElement>('button[data-menu]')!.click();
    await flush();
    expect(root.querySelector('button[data-recall]')).not.toBeNull();
    row('id2').querySelector<HTMLButtonElement>('button[data-menu]')!.click();
    await flush();
    expect(root.querySelector('button[data-recall]')).toBeNull();
    expect(root.querySelector('button[data-reply]')).not.toBeNull();
    expect(root.querySelector('button[data-copy]')).not.toBeNull();

    compose('chưa gửi được');
    submitForm();
    await flush();
    expect(root.querySelector('[data-msg="new-1"] button[data-menu]')).toBeNull();   // still on its way
  });

  it('offers no menu on a message that has already been taken back', async () => {
    open({}, [{ ...msg(1, 'mom', ''), deleted: true }]);
    await flush();
    expect(row('id1').querySelector('button[data-menu]')).toBeNull();
  });

  it('takes it back only after asking, then shows the placeholder at once', async () => {
    const h = open({ recall: vi.fn(async () => ok(undefined)) });
    await flush();

    row('id1').querySelector<HTMLButtonElement>('button[data-menu]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('button[data-recall]')!.click();
    await flush();
    expect(h.api.recall).not.toHaveBeenCalled();
    expect(text(root.querySelector('.menu')!)).toContain('mọi người');

    root.querySelector<HTMLButtonElement>('button[data-confirm-recall]')!.click();
    await flush();

    expect(h.api.recall).toHaveBeenCalledWith(C1, 'id1');
    expect(row('id1').classList.contains('recalled')).toBe(true);
    expect(root.querySelector('.menu')).toBeNull();
  });

  it('can change its mind', async () => {
    const h = open({ recall: vi.fn(async () => ok(undefined)) });
    await flush();
    row('id1').querySelector<HTMLButtonElement>('button[data-menu]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('button[data-recall]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('button[data-cancel-recall]')!.click();
    await flush();
    expect(root.querySelector('.menu')).toBeNull();
    expect(h.api.recall).not.toHaveBeenCalled();
    expect(row('id1').classList.contains('recalled')).toBe(false);
  });

  it('shows a refusal in words and leaves the message as it was', async () => {
    open({ recall: vi.fn(async () => fail('not_your_message', 403)) });
    await flush();
    row('id1').querySelector<HTMLButtonElement>('button[data-menu]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('button[data-recall]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('button[data-confirm-recall]')!.click();
    await flush();
    expect(root.querySelector('[role="alert"]')).not.toBeNull();
    expect(row('id1').classList.contains('recalled')).toBe(false);
  });

  it('turns a message into a placeholder when told live that someone took it back', async () => {
    const h = open();
    await flush();
    h.handlers().event(C1, recalled(theirs, 3));
    await flush();
    expect(row('id2').classList.contains('recalled')).toBe(true);
    expect(text(row('id2'))).not.toContain('tin của Minh');
  });

  it('does not ask for a catch-up when a recall takes a number but adds no message', async () => {
    const h = open();
    await flush();
    h.handlers().event(C1, recalled(theirs, 3));   // number 3 is the recall
    h.handlers().event(C1, created(msg(4, 'minh', 'tiếp theo')));
    await flush();
    expect(h.live.sync).not.toHaveBeenCalled();
    expect(bubbles().map((b) => b.getAttribute('data-msg'))).toEqual(['id1', 'id2', 'id4']);
  });

  it('starts from where the stream stood when the history was read, not from the last message', async () => {
    const h = open({}, [mine, theirs], { lastSeq: { [C1]: 4 } });   // numbers 3 and 4 were recalls
    await flush();
    h.handlers().event(C1, created(msg(5, 'minh', 'sau đó')));
    await flush();
    expect(h.live.sync).not.toHaveBeenCalled();
    h.handlers().event(C1, created(msg(7, 'minh', 'bỏ sót số 6')));
    await flush();
    expect(h.live.sync).toHaveBeenCalledWith(C1, 5);
  });

  it('catches up from the stream position after a reconnect', async () => {
    const h = open({}, [mine, theirs], { lastSeq: { [C1]: 4 } });
    await flush();
    h.handlers().state(false);
    h.handlers().state(true);
    await flush();
    expect(h.live.sync).toHaveBeenCalledWith(C1, 4);
  });

  it('updates the list when the last message is taken back, and takes it off the unread count once', async () => {
    const m = msg(2, 'minh', 'sắp thu hồi');
    const h = start({ chats: [withLast(m, { lastReadSeq: 1, unread: 1 })], history: {} });
    await flush();
    expect(rows()[0]!.querySelector('.badge')!.textContent).toBe('1');

    h.handlers().event(C1, recalled(m, 3));
    h.handlers().event(C1, recalled(m, 3));
    await flush();

    expect(text(rows()[0]!.querySelector('.pv')!)).toBe('Tin nhắn đã được thu hồi');
    expect(rows()[0]!.querySelector('.badge')).toBeNull();
  });

  it('leaves the unread count alone when the message taken back was yours, or had already been read', async () => {
    const readOne = msg(1, 'minh', 'đã đọc');
    const unreadOne = msg(3, 'minh', 'chưa đọc');
    const own = msg(4, 'mom', 'của tôi');   // after the read position, so only the sender check stops it counting
    const h = start({ chats: [withLast(unreadOne, { lastReadSeq: 2, unread: 1 })], history: {} });
    await flush();

    h.handlers().event(C1, recalled(readOne, 4));   // before the read position
    h.handlers().event(C1, recalled(own, 6));       // yours
    await flush();

    expect(rows()[0]!.querySelector('.badge')!.textContent).toBe('1');
  });

  it('shows your own last message as taken back in the list at once', async () => {
    const own = msg(1, 'mom', 'tôi viết');
    const h = start({ path: `/c/${C1}`, chats: [withLast(own, { lastReadSeq: 1 })], history: { [C1]: [own] }, api: { recall: vi.fn(async () => ok(undefined)) } });
    await flush();
    row('id1').querySelector<HTMLButtonElement>('button[data-menu]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('button[data-recall]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('button[data-confirm-recall]')!.click();
    await flush();
    expect(h.api.recall).toHaveBeenCalled();
    expect(text(rows()[0]!.querySelector('.pv')!)).toBe('Bạn đã thu hồi một tin nhắn');
  });

  it('does not move a conversation to the top because a message in it was taken back', async () => {
    const old = msg(1, 'minh', 'cũ', at(9), C2);
    const h = start({ chats: [withLast(msg(3, 'minh', 'mới', at(11))), withLast(old, { id: C2, title: 'Lan', peer: { gomId: 'lan', displayName: 'Lan' } })] });
    await flush();
    h.handlers().event(C2, recalled(old, 2));
    await flush();
    expect(rows().map((r) => r.getAttribute('data-chat'))).toEqual([C1, C2]);
  });

  it('shows a message that arrives already taken back as such, and does not count it as unread', async () => {
    const h = start({ chats: [chat(C1, 'Minh')] });
    await flush();
    h.handlers().event(C1, { seq: 1, kind: 'created', message: { ...msg(1, 'minh', ''), deleted: true } });
    await flush();
    expect(text(rows()[0]!.querySelector('.pv')!)).toBe('Tin nhắn đã được thu hồi');
    expect(rows()[0]!.querySelector('.badge')).toBeNull();
  });

  it('shows a recalled last message in the list as such, in your voice and theirs', async () => {
    const own = withLast(msg(1, 'mom'), { lastMessage: { seq: 1, body: '', createdAt: at(10), sender: { id: 'u-mom', displayName: 'Mẹ' }, deleted: true } });
    const their = chat(C2, 'Lan', { lastSeq: 1, lastMessage: { seq: 1, body: '', createdAt: at(10), sender: { id: 'u-lan', displayName: 'Lan' }, deleted: true } });
    start({ chats: [own, their] });
    await flush();
    expect(text(rows()[0]!.querySelector('.pv')!)).toBe('Bạn đã thu hồi một tin nhắn');
    expect(text(rows()[1]!.querySelector('.pv')!)).toBe('Tin nhắn đã được thu hồi');
  });
});


describe('chat: who has seen your message', () => {
  const mine1 = msg(1, 'mom', 'tôi viết');
  const theirs2 = msg(2, 'minh', 'bạn viết');
  const chats = [withLast(theirs2, { lastReadSeq: 2 })];
  const open = (reads: { enabled: boolean; reads: { userId: string; seq: number }[] }, history = [mine1, theirs2], extra: Record<string, unknown> = {}) =>
    start({ path: `/c/${C1}`, chats, history: { [C1]: history }, api: { reads: vi.fn(async () => ok(reads)), ...extra } });
  const seen = () => root.querySelector('.seen');
  const tick = (id: string) => root.querySelector(`[data-msg="${id}"] .tick`);

  it('marks your message with two checks once the other person has read up to it, and has no extra line in a chat of two', async () => {
    open({ enabled: true, reads: [{ userId: 'u-minh', seq: 1 }] });
    await flush();
    expect(tick('id1')!.classList.contains('t-seen')).toBe(true);
    expect(tick('id1')!.getAttribute('aria-label')).toBe('Đã xem');
    expect(seen()).toBeNull();
  });

  it('says nothing until they have read that far', async () => {
    open({ enabled: true, reads: [{ userId: 'u-minh', seq: 0 }] });
    await flush();
    expect(tick('id1')!.classList.contains('t-sent')).toBe(true);
    expect(tick('id1')!.getAttribute('aria-label')).toBe('Đã gửi');
  });

  it('says nothing when you have hidden yours, which hides theirs too', async () => {
    open({ enabled: false, reads: [] });
    await flush();
    expect(tick('id1')!.classList.contains('t-sent')).toBe(true);
  });

  it('marks each of your messages by how far they have read, and never marks theirs', async () => {
    open({ enabled: true, reads: [{ userId: 'u-minh', seq: 2 }] }, [mine1, theirs2, msg(3, 'mom', 'tin sau')]);
    await flush();
    expect(tick('id1')!.classList.contains('t-seen')).toBe(true);
    expect(tick('id3')!.classList.contains('t-sent')).toBe(true);
    expect(tick('id2')).toBeNull();
  });

  it('skips a message you took back, and shows nothing when you have written nothing', async () => {
    open({ enabled: true, reads: [{ userId: 'u-minh', seq: 9 }] }, [mine1, { ...msg(3, 'mom', ''), deleted: true }]);
    await flush();
    expect(tick('id3')).toBeNull();

    root = document.createElement('div'); document.body.replaceChildren(root);   // a fresh page
    open({ enabled: true, reads: [{ userId: 'u-minh', seq: 9 }] }, [theirs2]);
    await flush();
    expect(root.querySelector('.tick')).toBeNull();
  });

  it('does not show it for a message still on its way', async () => {
    open({ enabled: true, reads: [{ userId: 'u-minh', seq: 9 }] }, [theirs2], { send: vi.fn(async () => fail('network', 0)) });
    await flush();
    compose('chưa gửi được');
    submitForm();
    await flush();
    expect(root.querySelector('.mrow[data-state="queued"] .tick, .mrow[data-state="failed"] .tick, .mrow[data-state="sending"] .tick')).toBeNull();
  });

  it('shows it as soon as they read, with no reload', async () => {
    const h = open({ enabled: true, reads: [] });
    await flush();
    expect(tick('id1')!.classList.contains('t-sent')).toBe(true);
    h.handlers().read(C1, 'u-minh', 1);
    await flush();
    expect(tick('id1')!.classList.contains('t-seen')).toBe(true);
  });

  it('never goes backwards when an older reading arrives late', async () => {
    const h = open({ enabled: true, reads: [{ userId: 'u-minh', seq: 1 }] });
    await flush();
    h.handlers().read(C1, 'u-minh', 0);
    await flush();
    expect(tick('id1')!.classList.contains('t-seen')).toBe(true);
  });

  it('ignores a reading in a conversation that is not open, and one while you have it hidden', async () => {
    const h = open({ enabled: false, reads: [] });
    await flush();
    h.handlers().read(C1, 'u-minh', 1);
    await flush();
    expect(seen()).toBeNull();

    root = document.createElement('div'); document.body.replaceChildren(root);   // a fresh page
    const h2 = open({ enabled: true, reads: [] });
    await flush();
    h2.handlers().read(C2, 'u-minh', 1);
    await flush();
    expect(seen()).toBeNull();
  });

  it('asks again when told the list changed, because a switch may have been flipped', async () => {
    const h = open({ enabled: true, reads: [] });
    await flush();
    const before = (h.api.reads as ReturnType<typeof vi.fn>).mock.calls.length;
    h.handlers().conversations();
    await flush();
    expect((h.api.reads as ReturnType<typeof vi.fn>).mock.calls.length).toBe(before + 1);
  });

  it('asks again after the connection comes back', async () => {
    const h = open({ enabled: true, reads: [] });
    await flush();
    const before = (h.api.reads as ReturnType<typeof vi.fn>).mock.calls.length;
    h.handlers().state(false);
    h.handlers().state(true);
    await flush();
    expect((h.api.reads as ReturnType<typeof vi.fn>).mock.calls.length).toBeGreaterThan(before);
  });

  it('counts the people in a group who have seen it', async () => {
    const G = '33333333-3333-4333-8333-333333333333';
    const mineG: ChatMessage = { ...msg(1, 'mom', 'cả nhà ơi', at(10), G) };
    const h = start({
      path: `/c/${G}`, history: { [G]: [mineG] },
      chats: [{ id: G, kind: 'group', title: 'Nhóm', peer: null, lastSeq: 1, lastReadSeq: 1, unread: 0, lastMessage: null, memberCount: 4 }],
      api: { reads: vi.fn(async () => ok({ enabled: true, reads: [{ userId: 'u-a', seq: 1 }, { userId: 'u-b', seq: 1 }, { userId: 'u-c', seq: 0 }] })) },
    });
    await flush();
    expect(text(seen()!)).toBe('Đã xem bởi 2');
    h.handlers().read(G, 'u-a', 5);   // the same person reading further is still one person
    await flush();
    expect(text(seen()!)).toBe('Đã xem bởi 2');
    h.handlers().read(G, 'u-c', 1);   // a new person is one more
    await flush();
    expect(text(seen()!)).toBe('Đã xem bởi 3');
    expect(tick('id1')!.classList.contains('t-seen')).toBe(true);   // all three others have read it
  });

  it('keeps one check in a group until everyone else has read', async () => {
    const G = '33333333-3333-4333-8333-333333333333';
    start({
      path: `/c/${G}`, history: { [G]: [msg(1, 'mom', 'cả nhà ơi', at(10), G)] },
      chats: [{ id: G, kind: 'group', title: 'Nhóm', peer: null, lastSeq: 1, lastReadSeq: 1, unread: 0, lastMessage: null, memberCount: 4 }],
      api: { reads: vi.fn(async () => ok({ enabled: true, reads: [{ userId: 'u-a', seq: 1 }, { userId: 'u-b', seq: 1 }] })) },
    });
    await flush();
    expect(tick('id1')!.classList.contains('t-sent')).toBe(true);
    expect(text(seen()!)).toBe('Đã xem bởi 2');
  });
});

describe('chat: answering and copying', () => {
  const answer = (seq: number, from: 'mom' | 'minh', body: string, reply: ChatMessage['reply']): ChatMessage => ({ ...msg(seq, from, body), reply });
  const open = (extra: Record<string, unknown> = {}, messages: ChatMessage[] = [msg(1, 'minh', 'Tối nay ăn gì?'), msg(2, 'mom', 'tin của mẹ')]) =>
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh', { lastSeq: messages.length })], history: { [C1]: messages }, api: extra });
  const row = (id: string) => root.querySelector(`[data-msg="${id}"]`)!;
  const menuOf = async (id: string) => { row(id).querySelector<HTMLButtonElement>('button[data-menu]')!.click(); await flush(); };
  const type = (body: string) => { const t = root.querySelector<HTMLTextAreaElement>('textarea')!; t.value = body; t.dispatchEvent(new Event('input', { bubbles: true })); };
  const submit = () => root.querySelector<HTMLFormElement>('form.composer')!.dispatchEvent(new Event('submit', { cancelable: true, bubbles: true }));

  it('starts an answer from the menu: a bar above the composer names who and shows the start of the message', async () => {
    open();
    await flush();
    await menuOf('id1');
    root.querySelector<HTMLButtonElement>('button[data-reply]')!.click();
    await flush();
    const bar = root.querySelector('[data-replybar]')!;
    expect(text(bar)).toContain('Đang trả lời Minh');
    expect(text(bar)).toContain('Tối nay ăn gì?');
    expect(root.querySelector('.menu')).toBeNull();
  });

  it('says "chính bạn" when you answer your own message', async () => {
    open();
    await flush();
    await menuOf('id2');
    root.querySelector<HTMLButtonElement>('button[data-reply]')!.click();
    await flush();
    expect(text(root.querySelector('[data-replybar]')!)).toContain('Đang trả lời chính bạn');
  });

  it('cuts the bar at a hundred characters without breaking an emoji', async () => {
    const long = '👍'.repeat(150);
    open({}, [msg(1, 'minh', long)]);
    await flush();
    await menuOf('id1');
    root.querySelector<HTMLButtonElement>('button[data-reply]')!.click();
    await flush();
    expect(root.querySelector('.replybar .rb-text span')!.textContent).toBe('👍'.repeat(100));
  });

  it('sends the answer with the id of the message it answers, and the bar goes', async () => {
    // A real server gives back the very client id it was sent.
    const send = vi.fn(async (_c: string, clientId: string) => ok({ ...msg(3, 'mom', 'ăn phở'), clientId, reply: { id: 'id1', sender: { id: 'u-minh', displayName: 'Minh' }, snippet: 'Tối nay ăn gì?' } }));
    open({ send });
    await flush();
    await menuOf('id1');
    root.querySelector<HTMLButtonElement>('button[data-reply]')!.click();
    await flush();
    type('ăn phở');
    submit();
    await flush();
    expect(send).toHaveBeenCalledWith(C1, expect.any(String), 'ăn phở', 'id1');
    expect(root.querySelector('[data-replybar]')).toBeNull();
    expect(text(root.querySelector('[data-quote="id1"]')!)).toContain('Tối nay ăn gì?');
  });

  it('shows the quote at once on a message still on its way', async () => {
    open({ send: vi.fn(() => new Promise(() => undefined)) });
    await flush();
    await menuOf('id1');
    root.querySelector<HTMLButtonElement>('button[data-reply]')!.click();
    await flush();
    type('đang gửi');
    submit();
    await flush();
    expect(text(root.querySelector('[data-state="sending"] [data-quote]')!)).toContain('Minh');
  });

  it('cancels the answer with the close button', async () => {
    const send = vi.fn(async (_c: string, clientId: string) => ok({ ...msg(3, 'mom', 'x'), clientId }));
    open({ send });
    await flush();
    await menuOf('id1');
    root.querySelector<HTMLButtonElement>('button[data-reply]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('[data-cancel-reply]')!.click();
    await flush();
    expect(root.querySelector('[data-replybar]')).toBeNull();
    type('thường');
    submit();
    await flush();
    expect(send).toHaveBeenCalledWith(C1, expect.any(String), 'thường', undefined);
  });

  it('drops the bar when the message being answered is taken back', async () => {
    const h = open();
    await flush();
    await menuOf('id1');
    root.querySelector<HTMLButtonElement>('button[data-reply]')!.click();
    await flush();
    h.handlers().event(C1, recalled(msg(1, 'minh', 'Tối nay ăn gì?'), 3));
    await flush();
    expect(root.querySelector('[data-replybar]')).toBeNull();
  });

  it('shows a quote with who wrote it, and says so when the quoted message is not there to see', async () => {
    open({}, [
      answer(1, 'minh', 'đáp một', { id: 'x1', sender: { id: 'u-mom', displayName: 'Mẹ' }, snippet: 'gốc của mẹ' }),
      answer(2, 'minh', 'đáp hai', { id: 'x2', sender: null, snippet: null }),
    ]);
    await flush();
    const q1 = text(root.querySelector('[data-quote="x1"]')!);
    expect(q1).toContain('Bạn');
    expect(q1).toContain('gốc của mẹ');
    const q2 = text(root.querySelector('[data-quote="x2"]')!);
    expect(q2).toContain('Tin nhắn không còn hiển thị');
  });

  it('keeps the quote in the outbox so an answer written offline is still an answer after a reload', async () => {
    const h = open({ send: vi.fn(async () => fail('network', 0)) });
    await flush();
    await menuOf('id1');
    root.querySelector<HTMLButtonElement>('button[data-reply]')!.click();
    await flush();
    type('trả lời lúc mất mạng');
    submit();
    await flush();
    const saved = h.outbox.items as unknown as { body: string; replyTo?: { id: string; snippet: string } }[];
    expect(saved[0]?.body).toBe('trả lời lúc mất mạng');
    expect(saved[0]?.replyTo).toMatchObject({ id: 'id1', snippet: 'Tối nay ăn gì?' });
  });

  it('copies the text of a message and says so', async () => {
    const writeText = vi.fn(async () => undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    open();
    await flush();
    await menuOf('id1');
    root.querySelector<HTMLButtonElement>('button[data-copy]')!.click();
    await flush();
    expect(writeText).toHaveBeenCalledWith('Tối nay ăn gì?');
    expect(text(root)).toContain('Đã sao chép tin nhắn');
  });

  it('says when copying is not possible', async () => {
    Object.defineProperty(navigator, 'clipboard', { value: { writeText: vi.fn(async () => { throw new Error('no'); }) }, configurable: true });
    open();
    await flush();
    await menuOf('id1');
    root.querySelector<HTMLButtonElement>('button[data-copy]')!.click();
    await flush();
    expect(text(root)).toContain('Không sao chép được');
  });
});

describe('chat: passing a message on', () => {
  const chats = [chat(C1, 'Minh', { lastSeq: 2 }), chat(C2, 'Lan')];
  const open = (extra: Record<string, unknown> = {}, messages: ChatMessage[] = [msg(1, 'minh', 'Tối nay ăn gì?'), msg(2, 'mom', 'tin của mẹ')]) =>
    start({ path: `/c/${C1}`, chats, history: { [C1]: messages }, api: extra });
  const row = (id: string) => root.querySelector(`[data-msg="${id}"]`)!;
  const pick = async (id: string) => {
    row(id).querySelector<HTMLButtonElement>('button[data-menu]')!.click(); await flush();
    root.querySelector<HTMLButtonElement>('button[data-forward]')!.click(); await flush();
  };
  const echo = vi.fn(async (conversation: string, clientId: string, body: string) => ok({ ...msg(1, 'mom', body, at(11), conversation), id: `fw-${clientId}`, clientId }));

  it('lists your conversations to choose from, and shows the start of the message', async () => {
    open();
    await flush();
    await pick('id1');
    const panel = root.querySelector('[data-forward-panel]')!;
    expect(text(panel)).toContain('Tối nay ăn gì?');
    expect([...panel.querySelectorAll('[data-forward-to]')].map((b) => b.getAttribute('data-forward-to'))).toEqual([C1, C2]);
    expect(root.querySelector('.menu')).toBeNull();
  });

  it('sends the words to the chosen conversation as a new message with no answer attached, and says so', async () => {
    const send = echo.mockClear();
    open({ send });
    await flush();
    await pick('id1');
    root.querySelector<HTMLButtonElement>(`[data-forward-to="${C2}"]`)!.click();
    await flush();
    expect(send).toHaveBeenCalledTimes(1);
    expect(send).toHaveBeenCalledWith(C2, expect.any(String), 'Tối nay ăn gì?', undefined);
    expect(text(root)).toContain('Đã chuyển tiếp đến Lan');
    expect(root.querySelector('[data-forward-panel]')).toBeNull();
  });

  it('passes on a message that was itself an answer without its quote', async () => {
    const send = echo.mockClear();
    const answered: ChatMessage = { ...msg(1, 'minh', 'đáp'), reply: { id: 'x', sender: { id: 'u-mom', displayName: 'Mẹ' }, snippet: 'gốc' } };
    open({ send }, [answered]);
    await flush();
    await pick('id1');
    root.querySelector<HTMLButtonElement>(`[data-forward-to="${C2}"]`)!.click();
    await flush();
    expect(send).toHaveBeenCalledWith(C2, expect.any(String), 'đáp', undefined);
  });

  it('cancels without sending anything', async () => {
    const send = echo.mockClear();
    open({ send });
    await flush();
    await pick('id1');
    root.querySelector<HTMLButtonElement>('[data-cancel-forward]')!.click();
    await flush();
    expect(send).not.toHaveBeenCalled();
    expect(root.querySelector('[data-forward-panel]')).toBeNull();
  });

  it('tells you when it was refused for good, and leaves nothing waiting in the other conversation', async () => {
    const h = open({ send: vi.fn(async () => fail('not_friends', 403)) });
    await flush();
    await pick('id1');
    root.querySelector<HTMLButtonElement>(`[data-forward-to="${C2}"]`)!.click();
    await flush();
    expect(text(root.querySelector('.note')!)).toContain('bạn bè');
    expect(h.outbox.items).toHaveLength(0);
  });

  it('leaves no failed message behind in the conversation it was refused by', async () => {
    open({ send: vi.fn(async () => fail('not_friends', 403)) });
    await flush();
    await pick('id1');
    root.querySelector<HTMLButtonElement>(`[data-forward-to="${C2}"]`)!.click();
    await flush();
    root.querySelector<HTMLAnchorElement>(`a[data-chat="${C2}"]`)!.click();
    await flush();
    expect(root.querySelector('[data-state="failed"]')).toBeNull();
    expect(root.querySelector('[data-msg]')).toBeNull();
  });

  it('writes it down before the send finishes, so closing the page does not lose it', async () => {
    const h = open({ send: vi.fn(() => new Promise(() => undefined)) });
    await flush();
    await pick('id1');
    root.querySelector<HTMLButtonElement>(`[data-forward-to="${C2}"]`)!.click();
    await flush();
    expect(h.outbox.items).toHaveLength(1);
    expect(h.outbox.items[0]).toMatchObject({ conversation: C2, body: 'Tối nay ăn gì?' });
  });

  it('keeps it waiting, and says so, when the network is down', async () => {
    const h = open({ send: vi.fn(async () => fail('network', 0)) });
    await flush();
    await pick('id1');
    root.querySelector<HTMLButtonElement>(`[data-forward-to="${C2}"]`)!.click();
    await flush();
    expect(text(root.querySelector('.note')!)).toContain('Sẽ gửi khi có mạng');
    expect(h.outbox.items).toHaveLength(1);
    expect(h.outbox.items[0]).toMatchObject({ conversation: C2, body: 'Tối nay ăn gì?' });
  });

  it('offers no forwarding on a message taken back', async () => {
    open({}, [{ ...msg(1, 'minh', ''), deleted: true }]);
    await flush();
    expect(row('id1').querySelector('button[data-menu]')).toBeNull();
  });
});

describe('chat: muting', () => {
  const chats = [chat(C1, 'Minh', { lastSeq: 3, unread: 2, muted: true }), chat(C2, 'Lan', { lastSeq: 1, unread: 1 })];
  const open = (extra: Record<string, unknown> = {}, path = `/c/${C2}`) => start({ path, chats, history: { [C1]: [msg(1, 'minh')], [C2]: [msg(1, 'minh', 'x', at(10, 1), C2)] }, api: extra });
  const bell = () => root.querySelector<HTMLButtonElement>('.th-head button[data-mute]')!;

  it('marks a muted conversation in the list and greys its badge, and not the others', async () => {
    open({}, '/');
    await flush();
    const muted = root.querySelector(`[data-chat="${C1}"]`)!;
    const loud = root.querySelector(`[data-chat="${C2}"]`)!;
    expect(muted.querySelector('.mute-mark')).not.toBeNull();
    expect(muted.querySelector('.badge')!.classList.contains('muted')).toBe(true);
    expect(loud.querySelector('.mute-mark')).toBeNull();
    expect(loud.querySelector('.badge')!.classList.contains('muted')).toBe(false);
  });

  it('shows the switch in the thread with the state it is in', async () => {
    open({}, `/c/${C1}`);
    await flush();
    expect(bell().getAttribute('aria-pressed')).toBe('true');
    expect(bell().getAttribute('aria-label')).toBe('Bật lại thông báo');
  });

  it('mutes at once and asks the server', async () => {
    const h = open();
    await flush();
    expect(bell().getAttribute('aria-pressed')).toBe('false');
    bell().click();
    expect(bell().getAttribute('aria-pressed')).toBe('true');
    await flush();
    expect(h.api.setMute).toHaveBeenCalledWith(C2, true);
    expect(root.querySelector(`[data-chat="${C2}"] .mute-mark`)).not.toBeNull();
  });

  it('unmutes the same way', async () => {
    const h = open({}, `/c/${C1}`);
    await flush();
    bell().click();
    await flush();
    expect(h.api.setMute).toHaveBeenCalledWith(C1, false);
    expect(bell().getAttribute('aria-pressed')).toBe('false');
  });

  it('puts the switch back and says why when the server refuses', async () => {
    open({ setMute: vi.fn(async () => fail('server', 500)) });
    await flush();
    bell().click();
    await flush();
    expect(bell().getAttribute('aria-pressed')).toBe('false');
    expect(root.querySelector('[role="alert"]')).not.toBeNull();
  });
});

describe('chat: deleting a message for yourself', () => {
  const messages = [msg(1, 'minh', 'của Minh'), msg(2, 'mom', 'của mẹ')];
  const open = (extra: Record<string, unknown> = {}) => start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh', { lastSeq: 2 })], history: { [C1]: messages }, api: extra });
  const row = (id: string) => root.querySelector(`[data-msg="${id}"]`);
  const menuOf = async (id: string) => { row(id)!.querySelector<HTMLButtonElement>('button[data-menu]')!.click(); await flush(); };

  it('is offered on someone else\'s message and on your own', async () => {
    open();
    await flush();
    for (const id of ['id1', 'id2']) {
      await menuOf(id);
      expect(root.querySelector('button[data-hide]')).not.toBeNull();
    }
  });

  it('asks first and says the others still see it, and does nothing until yes', async () => {
    const h = open();
    await flush();
    await menuOf('id1');
    root.querySelector<HTMLButtonElement>('button[data-hide]')!.click();
    await flush();
    expect(text(root.querySelector('.menu')!)).toContain('Người khác vẫn thấy');
    expect(h.api.hideMessage).not.toHaveBeenCalled();
    expect(row('id1')).not.toBeNull();
  });

  it('removes it from view after yes, and asks the server and the list again', async () => {
    const h = open();
    await flush();
    await menuOf('id1');
    root.querySelector<HTMLButtonElement>('button[data-hide]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('[data-confirm-hide]')!.click();
    await flush();
    expect(h.api.hideMessage).toHaveBeenCalledWith(C1, 'id1');
    expect(row('id1')).toBeNull();
    expect(row('id2')).not.toBeNull();
    expect(h.api.conversations).toHaveBeenCalledTimes(2);
  });

  it('keeps it and says why when the server refuses', async () => {
    open({ hideMessage: vi.fn(async () => fail('server', 500)) });
    await flush();
    await menuOf('id1');
    root.querySelector<HTMLButtonElement>('button[data-hide]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('[data-confirm-hide]')!.click();
    await flush();
    expect(row('id1')).not.toBeNull();
    expect(root.querySelector('[role="alert"]')).not.toBeNull();
  });

  it('cancels without asking the server', async () => {
    const h = open();
    await flush();
    await menuOf('id1');
    root.querySelector<HTMLButtonElement>('button[data-hide]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('[data-cancel-hide]')!.click();
    await flush();
    expect(h.api.hideMessage).not.toHaveBeenCalled();
    expect(root.querySelector('.menu')).toBeNull();
    expect(row('id1')).not.toBeNull();
  });

  it('drops a message when another device of yours deleted it', async () => {
    const h = open();
    await flush();
    h.handlers().hidden(C1, 'id2');
    await flush();
    expect(row('id2')).toBeNull();
    expect(row('id1')).not.toBeNull();
  });

  it('drops the bar above the composer if the message being answered was deleted for you', async () => {
    const h = open();
    await flush();
    await menuOf('id1');
    root.querySelector<HTMLButtonElement>('button[data-reply]')!.click();
    await flush();
    expect(root.querySelector('[data-replybar]')).not.toBeNull();
    h.handlers().hidden(C1, 'id1');
    await flush();
    expect(root.querySelector('[data-replybar]')).toBeNull();
  });
});

describe('chat: where reading starts', () => {
  const scrolled: Element[] = [];
  beforeEach(() => {
    scrolled.length = 0;
    (Element.prototype as unknown as { scrollIntoView: (o?: unknown) => void }).scrollIntoView = function (this: Element) { scrolled.push(this); };
  });
  const history = { [C1]: [msg(1, 'mom'), msg(2, 'minh'), msg(3, 'minh')] };
  const unreadChats = [withLast(msg(3, 'minh'), { lastReadSeq: 1, unread: 2 }), chat(C2, 'Lan')];

  it('scrolls to the first unread message when a conversation with unread messages is opened', async () => {
    start({ path: `/c/${C1}`, chats: unreadChats, history });
    await flush();
    expect(scrolled).toHaveLength(1);
    expect(scrolled[0]!.classList.contains('newdiv')).toBe(true);
  });

  it('does not scroll to a divider when nothing is unread', async () => {
    start({ path: `/c/${C1}`, chats: [withLast(msg(3, 'minh'), { lastReadSeq: 3, unread: 0 })], history });
    await flush();
    expect(scrolled).toHaveLength(0);
  });

  it('jumps once, and a new message afterwards does not drag you back to the divider', async () => {
    const h = start({ path: `/c/${C1}`, chats: unreadChats, history });
    await flush();
    h.handlers().event(C1, created(msg(4, 'minh', 'mới')));
    await flush();
    expect(scrolled).toHaveLength(1);
  });

  it('also jumps when you open a conversation you had already loaded', async () => {
    const h = start({ chats: unreadChats, history });
    await flush();
    root.querySelectorAll<HTMLAnchorElement>('a[data-chat]')[0]!.click();
    await flush();
    expect(scrolled).toHaveLength(1);
    expect(h.nav.push).toHaveBeenCalledWith(`/c/${C1}`);
  });

  it('says how many new messages arrived while you read further up, and the button takes you down', async () => {
    const h = start({ path: `/c/${C1}`, chats: unreadChats, history });
    await flush();
    expect(root.querySelector('[data-jump-bottom]')).toBeNull();
    h.handlers().event(C1, created(msg(4, 'minh', 'mới một')));
    h.handlers().event(C1, created(msg(5, 'minh', 'mới hai')));
    await flush();
    const pill = root.querySelector<HTMLButtonElement>('[data-jump-bottom]')!;
    expect(text(pill)).toContain('2 tin mới');
    pill.click();
    await flush();
    expect(root.querySelector('[data-jump-bottom]')).toBeNull();
  });

  it('does not count your own messages or a repeat of one already shown', async () => {
    const h = start({ path: `/c/${C1}`, chats: unreadChats, history });
    await flush();
    h.handlers().event(C1, created(msg(4, 'mom', 'của mẹ')));
    h.handlers().event(C1, created(msg(3, 'minh')));
    await flush();
    expect(root.querySelector('[data-jump-bottom]')).toBeNull();
  });

  it('takes the button away when you scroll down to the end yourself', async () => {
    const h = start({ path: `/c/${C1}`, chats: unreadChats, history });
    await flush();
    h.handlers().event(C1, created(msg(4, 'minh', 'mới')));
    await flush();
    expect(root.querySelector('[data-jump-bottom]')).not.toBeNull();
    root.querySelector('.msgs')!.dispatchEvent(new Event('scroll'));   // jsdom has no layout: every box is "at the bottom"
    await flush();
    expect(root.querySelector('[data-jump-bottom]')).toBeNull();
  });

  it('shows no button while you are at the bottom', async () => {
    const h = start({ path: `/c/${C1}`, chats: [withLast(msg(3, 'minh'), { lastReadSeq: 3, unread: 0 })], history });
    await flush();
    h.handlers().event(C1, created(msg(4, 'minh', 'mới')));
    await flush();
    expect(root.querySelector('[data-jump-bottom]')).toBeNull();
  });
});

describe('chat: searching a conversation', () => {
  const scrolled: Element[] = [];
  beforeEach(() => {
    scrolled.length = 0;
    (Element.prototype as unknown as { scrollIntoView: (o?: unknown) => void }).scrollIntoView = function (this: Element) { scrolled.push(this); };
  });
  const messages = [msg(1, 'minh', 'Tối nay ăn phở'), msg(2, 'mom', 'ok')];
  const hit = (m: ChatMessage): ChatMessage => m;
  const open = (extra: Record<string, unknown> = {}) => start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh', { lastSeq: 2 })], history: { [C1]: messages }, api: extra });
  const toggle = async () => { root.querySelector<HTMLButtonElement>('.th-head button[data-search]')!.click(); await flush(); };
  const ask = async (q: string) => {
    const input = root.querySelector<HTMLInputElement>('.searchbar input')!;
    input.value = q; input.dispatchEvent(new Event('input', { bubbles: true }));
    root.querySelector<HTMLFormElement>('[data-search-form]')!.dispatchEvent(new Event('submit', { cancelable: true, bubbles: true }));
    await flush();
  };

  it('opens a search bar from the header and closes it again', async () => {
    open();
    await flush();
    expect(root.querySelector('.searchbar')).toBeNull();
    await toggle();
    expect(root.querySelector('.searchbar input')).not.toBeNull();
    await toggle();
    expect(root.querySelector('.searchbar')).toBeNull();
  });

  it('asks the server about this conversation and lists who said it, when, and the words', async () => {
    const search = vi.fn(async () => ok({ results: [hit(messages[0]!)] }));
    open({ search });
    await flush();
    await toggle();
    await ask('  phở ');
    expect(search).toHaveBeenCalledWith(C1, 'phở');
    const row = root.querySelector('[data-result="id1"]')!;
    expect(text(row)).toContain('Minh');
    expect(text(row)).toContain('Tối nay ăn phở');
  });

  it('says so when nothing was found', async () => {
    open();
    await flush();
    await toggle();
    await ask('xyz');
    expect(root.querySelector('[data-no-results]')).not.toBeNull();
  });

  it('closes the search with the cross inside the field', async () => {
    open();
    await flush();
    await toggle();
    root.querySelector<HTMLButtonElement>('.searchbar [data-close-search]')!.click();
    await flush();
    expect(root.querySelector('.searchbar')).toBeNull();
  });

  it('does not ask for an empty search', async () => {
    const h = open();
    await flush();
    await toggle();
    await ask('   ');
    expect(h.api.search).not.toHaveBeenCalled();
  });

  it('shows what went wrong when the search fails', async () => {
    open({ search: vi.fn(async () => fail('server', 500)) });
    await flush();
    await toggle();
    await ask('phở');
    expect(root.querySelector('[role="alert"]')).not.toBeNull();
  });

  it('closes on Escape', async () => {
    open();
    await flush();
    await toggle();
    root.querySelector('.searchbar input')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await flush();
    expect(root.querySelector('.searchbar')).toBeNull();
  });

  it('lets only the newest answer fill the list, even if an older one comes last', async () => {
    const finish: Array<(v: unknown) => void> = [];
    const search = vi.fn(() => new Promise((resolve) => { finish.push(resolve); }));
    open({ search });
    await flush();
    await toggle();
    await ask('một');
    await ask('hai');
    finish[1]!(ok({ results: [hit(messages[1]!)] })); await flush();
    finish[0]!(ok({ results: [hit(messages[0]!)] })); await flush();
    expect(root.querySelector('[data-result="id2"]')).not.toBeNull();
    expect(root.querySelector('[data-result="id1"]')).toBeNull();
  });

  it('takes you to a result that is already on screen, and marks it for a moment', async () => {
    const h = open({ search: vi.fn(async () => ok({ results: [hit(messages[0]!)] })) });
    await flush();
    await toggle();
    await ask('phở');
    scrolled.length = 0;
    root.querySelector<HTMLButtonElement>('[data-result="id1"]')!.click();
    await flush();
    expect(root.querySelector('.searchbar')).toBeNull();
    expect(scrolled.some((e) => e.getAttribute('data-msg') === 'id1')).toBe(true);
    expect(root.querySelector('[data-msg="id1"]')!.classList.contains('found')).toBe(true);
    h.timers[h.timers.length - 1]!.fn();
    await flush();
    expect(root.querySelector('[data-msg="id1"]')!.classList.contains('found')).toBe(false);
  });

  it('loads older messages until a result that is further back is there', async () => {
    const old = msg(1, 'minh', 'xa lắm phở');
    const history = vi.fn(async (_id: string, before?: number) => before === undefined
      ? ok({ messages: [msg(3, 'minh'), msg(2, 'mom')], hasMore: true, lastSeq: 3 })
      : ok({ messages: [old], hasMore: false, lastSeq: 3 }));
    open({ history, search: vi.fn(async () => ok({ results: [hit(old)] })) });
    await flush();
    expect(root.querySelector('[data-msg="id1"]')).toBeNull();
    await toggle();
    await ask('phở');
    root.querySelector<HTMLButtonElement>('[data-result="id1"]')!.click();
    await flush();
    expect(history).toHaveBeenCalledWith(C1, 2);
    expect(root.querySelector('[data-msg="id1"]')!.classList.contains('found')).toBe(true);
  });

  it('drops a result from the list when you delete that message for yourself', async () => {
    const h = open({ search: vi.fn(async () => ok({ results: [hit(messages[0]!)] })) });
    await flush();
    await toggle();
    await ask('phở');
    h.handlers().hidden(C1, 'id1');
    await flush();
    expect(root.querySelector('[data-result="id1"]')).toBeNull();
  });
});

describe('chat: pinned messages', () => {
  const scrolled: Element[] = [];
  beforeEach(() => {
    scrolled.length = 0;
    (Element.prototype as unknown as { scrollIntoView: (o?: unknown) => void }).scrollIntoView = function (this: Element) { scrolled.push(this); };
  });
  const messages = [msg(1, 'minh', 'quy tắc nhà'), msg(2, 'mom', 'tin của mẹ'), msg(3, 'minh', 'địa chỉ mới')];
  const open = (extra: Record<string, unknown> = {}) => start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh', { lastSeq: 3 })], history: { [C1]: messages }, api: extra });
  const menuOf = async (id: string) => { root.querySelector(`[data-msg="${id}"] button[data-menu]`)!.dispatchEvent(new MouseEvent('click', { bubbles: true })); await flush(); };

  it('shows nothing when nothing is pinned', async () => {
    open();
    await flush();
    expect(root.querySelector('[data-pinbar]')).toBeNull();
  });

  it('shows the newest pin under the header, and a count when there are more', async () => {
    open({ pins: vi.fn(async () => ok({ pins: [messages[2]!, messages[0]!] })) });
    await flush();
    expect(text(root.querySelector('[data-pin-top]')!)).toContain('địa chỉ mới');
    expect(text(root.querySelector('[data-pin-all]')!)).toContain('2 tin');
    expect(root.querySelector('[data-pinlist]')).toBeNull();
    root.querySelector<HTMLButtonElement>('[data-pin-all]')!.click();
    await flush();
    expect([...root.querySelectorAll('[data-pin-item]')].map((b) => b.getAttribute('data-pin-item'))).toEqual(['id3', 'id1']);
  });

  it('closes the list when you go to one of the pins in it', async () => {
    open({ pins: vi.fn(async () => ok({ pins: [messages[2]!, messages[0]!] })) });
    await flush();
    root.querySelector<HTMLButtonElement>('[data-pin-all]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('[data-pin-item="id1"]')!.click();
    await flush();
    expect(root.querySelector('[data-pinlist]')).toBeNull();
  });

  it('has no list button when only one message is pinned', async () => {
    open({ pins: vi.fn(async () => ok({ pins: [messages[0]!] })) });
    await flush();
    expect(root.querySelector('[data-pin-all]')).toBeNull();
  });

  it('offers "Ghim" on an unpinned message and "Bỏ ghim" on a pinned one', async () => {
    open({ pins: vi.fn(async () => ok({ pins: [messages[0]!] })) });
    await flush();
    await menuOf('id2');
    expect(root.querySelector('button[data-pin]')).not.toBeNull();
    expect(root.querySelector('button[data-unpin]')).toBeNull();
    await menuOf('id1');
    expect(root.querySelector('button[data-unpin]')).not.toBeNull();
    expect(root.querySelector('button[data-pin]')).toBeNull();
  });

  it('pins, then asks for the pins again', async () => {
    const h = open();
    await flush();
    await menuOf('id2');
    root.querySelector<HTMLButtonElement>('button[data-pin]')!.click();
    await flush();
    expect(h.api.pin).toHaveBeenCalledWith(C1, 'id2');
    expect(h.api.pins).toHaveBeenCalledTimes(2);
  });

  it('unpins the same way', async () => {
    const h = open({ pins: vi.fn(async () => ok({ pins: [messages[0]!] })) });
    await flush();
    await menuOf('id1');
    root.querySelector<HTMLButtonElement>('button[data-unpin]')!.click();
    await flush();
    expect(h.api.unpin).toHaveBeenCalledWith(C1, 'id1');
  });

  it('says why when pinning is refused, for instance at the limit of three', async () => {
    open({ pin: vi.fn(async () => fail('pin_limit', 409)) });
    await flush();
    await menuOf('id2');
    root.querySelector<HTMLButtonElement>('button[data-pin]')!.click();
    await flush();
    expect(text(root.querySelector('[role="alert"]')!)).toContain('tối đa 3');
  });

  it('asks for the pins again when the server says they changed, but only for a conversation that is open', async () => {
    const h = open();
    await flush();
    const calls = h.api.pins.mock.calls.length;
    h.handlers().pins(C1);
    await flush();
    expect(h.api.pins.mock.calls.length).toBe(calls + 1);
    h.handlers().pins(C2);
    await flush();
    expect(h.api.pins.mock.calls.length).toBe(calls + 1);
  });

  it('takes you to the pinned message and marks it for a moment', async () => {
    open({ pins: vi.fn(async () => ok({ pins: [messages[0]!] })) });
    await flush();
    scrolled.length = 0;
    root.querySelector<HTMLButtonElement>('[data-pin-top]')!.click();
    await flush();
    expect(scrolled.some((e) => e.getAttribute('data-msg') === 'id1')).toBe(true);
    expect(root.querySelector('[data-msg="id1"]')!.classList.contains('found')).toBe(true);
  });

  it('loads older pages until a pinned message that is further back is there', async () => {
    const old = msg(1, 'minh', 'xa lắm');
    const history = vi.fn(async (_id: string, before?: number) => before === undefined
      ? ok({ messages: [msg(3, 'minh'), msg(2, 'mom')], hasMore: true, lastSeq: 3 })
      : ok({ messages: [old], hasMore: false, lastSeq: 3 }));
    open({ history, pins: vi.fn(async () => ok({ pins: [old] })) });
    await flush();
    expect(root.querySelector('[data-msg="id1"]')).toBeNull();
    root.querySelector<HTMLButtonElement>('[data-pin-top]')!.click();
    await flush();
    expect(history).toHaveBeenCalledWith(C1, 2);
    expect(root.querySelector('[data-msg="id1"]')!.classList.contains('found')).toBe(true);
  });
});

describe('chat: reactions', () => {
  const minhReacts = (emoji: string, who = [{ id: 'u-minh', displayName: 'Minh' }]) => ({ emoji, users: who });
  const base = [msg(1, 'minh', 'tin của Minh'), msg(2, 'mom', 'tin của mẹ')];
  const open = (extra: Record<string, unknown> = {}, messages: ChatMessage[] = base) =>
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh', { lastSeq: 2, lastMessage: { seq: 2, body: 'tin của mẹ', createdAt: at(10, 2), sender: { id: 'u-mom', displayName: 'Mẹ' } } })], history: { [C1]: messages }, api: extra });
  const row = (id: string) => root.querySelector(`[data-msg="${id}"]`)!;
  const menuOf = async (id: string) => { row(id).querySelector<HTMLButtonElement>('button[data-menu]')!.click(); await flush(); };

  it('shows each emoji under a message with how many chose it, and who', async () => {
    open({}, [{ ...base[0]!, reactions: [minhReacts('👍', [{ id: 'u-minh', displayName: 'Minh' }, { id: 'u-lan', displayName: 'Lan' }]), minhReacts('❤️')] }, base[1]!]);
    await flush();
    const chips = [...root.querySelectorAll('[data-chip]')];
    expect(chips.map((c) => c.getAttribute('data-chip'))).toEqual(['👍', '❤️']);
    expect(text(chips[0]!)).toContain('2');
    expect(chips[0]!.getAttribute('aria-label')).toBe('👍: Minh, Lan');
  });

  it('marks your own reaction and names you "Bạn" for a screen reader', async () => {
    open({}, [{ ...base[0]!, reactions: [minhReacts('👍', [{ id: 'u-mom', displayName: 'Mẹ' }, { id: 'u-minh', displayName: 'Minh' }])] }, base[1]!]);
    await flush();
    const chip = root.querySelector('[data-chip="👍"]')!;
    expect(chip.getAttribute('aria-pressed')).toBe('true');
    expect(chip.getAttribute('aria-label')).toBe('👍: Bạn, Minh');
  });

  it('shows no chips on a message with none, or one taken back', async () => {
    open({}, [{ ...base[0]!, reactions: [] }, { ...base[1]!, deleted: true, reactions: [minhReacts('👍')] }]);
    await flush();
    expect(root.querySelector('[data-chip]')).toBeNull();
  });

  it('offers the six emoji in the menu and sends the one chosen', async () => {
    const h = open();
    await flush();
    await menuOf('id1');
    expect([...root.querySelectorAll('[data-react]')].map((b) => b.getAttribute('data-react'))).toEqual(['👍', '❤️', '😆', '😮', '😢', '😡']);
    root.querySelector<HTMLButtonElement>('[data-react="😆"]')!.click();
    await flush();
    expect(h.api.react).toHaveBeenCalledWith(C1, 'id1', '😆');
    expect(root.querySelector('.menu')).toBeNull();
  });

  it('chooses an emoji by tapping a chip you have not chosen, and takes yours back by tapping it again', async () => {
    const h = open({}, [{ ...base[0]!, reactions: [minhReacts('👍'), minhReacts('❤️', [{ id: 'u-mom', displayName: 'Mẹ' }])] }, base[1]!]);
    await flush();
    root.querySelector<HTMLButtonElement>('[data-chip="👍"]')!.click();
    await flush();
    expect(h.api.react).toHaveBeenCalledWith(C1, 'id1', '👍');
    root.querySelector<HTMLButtonElement>('[data-chip="❤️"]')!.click();
    await flush();
    expect(h.api.unreact).toHaveBeenCalledWith(C1, 'id1');
  });

  it('says why when the server refuses', async () => {
    open({ react: vi.fn(async () => fail('not_friends', 403)) });
    await flush();
    await menuOf('id1');
    root.querySelector<HTMLButtonElement>('[data-react="👍"]')!.click();
    await flush();
    expect(root.querySelector('[role="alert"]')).not.toBeNull();
  });

  it('shows a reaction that arrives live without moving the list or counting as unread', async () => {
    const h = open();
    await flush();
    const updated = { seq: 3, kind: 'updated' as const, message: { ...base[0]!, reactions: [minhReacts('😮')] } };
    h.handlers().event(C1, updated);
    await flush();
    expect(root.querySelector('[data-chip="😮"]')).not.toBeNull();
    expect(root.querySelector(`[data-chat="${C1}"] .badge`)).toBeNull();
    expect(text(root.querySelector(`[data-chat="${C1}"] .pv`)!)).toContain('tin của mẹ');
  });

  it('follows the words of the last message in the list when they change, and nothing else moves', async () => {
    const h = open();
    await flush();
    h.handlers().event(C1, { seq: 3, kind: 'updated', message: { ...base[1]!, body: 'đã sửa' } });
    await flush();
    expect(text(root.querySelector(`[data-chat="${C1}"] .pv`)!)).toContain('đã sửa');
  });

  it('does not add a changed message that this device never loaded', async () => {
    const h = start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh', { lastSeq: 9 })], history: { [C1]: [msg(8, 'minh'), msg(9, 'mom')] }, hasMore: true });
    await flush();
    h.handlers().event(C1, { seq: 10, kind: 'updated', message: { ...msg(2, 'minh', 'cũ'), reactions: [minhReacts('👍')] } });
    await flush();
    expect(root.querySelector('[data-msg="id2"]')).toBeNull();
    expect([...root.querySelectorAll('[data-msg]')].map((m) => m.getAttribute('data-msg'))).toEqual(['id8', 'id9']);
  });

  it('takes the cursor forward over an updated event so nothing is asked for twice', async () => {
    const h = open();
    await flush();
    h.handlers().event(C1, { seq: 3, kind: 'updated', message: { ...base[0]!, reactions: [minhReacts('👍')] } });
    await flush();
    expect(h.live.sync).not.toHaveBeenCalled();
    h.handlers().event(C1, { seq: 5, kind: 'updated', message: { ...base[0]!, reactions: [minhReacts('❤️')] } });
    await flush();
    expect(h.live.sync).toHaveBeenCalledWith(C1, 3);
  });
});

describe('chat: editing a message', () => {
  const base = [msg(1, 'minh', 'tin của Minh'), msg(2, 'mom', 'tin của mẹ')];
  const open = (extra: Record<string, unknown> = {}, messages: ChatMessage[] = base) =>
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh', { lastSeq: 2 })], history: { [C1]: messages }, api: extra });
  const row = (id: string) => root.querySelector(`[data-msg="${id}"]`)!;
  const menuOf = async (id: string) => { row(id).querySelector<HTMLButtonElement>('button[data-menu]')!.click(); await flush(); };
  const field = () => root.querySelector<HTMLTextAreaElement>('.composer textarea')!;
  const type = (body: string) => { field().value = body; field().dispatchEvent(new Event('input', { bubbles: true })); };
  const submit = () => root.querySelector<HTMLFormElement>('form.composer')!.dispatchEvent(new Event('submit', { cancelable: true, bubbles: true }));

  it('offers "Chỉnh sửa" on your own message and not on someone else\'s', async () => {
    open();
    await flush();
    await menuOf('id2');
    expect(root.querySelector('button[data-edit]')).not.toBeNull();
    await menuOf('id1');
    expect(root.querySelector('button[data-edit]')).toBeNull();
  });

  it('puts the words in the composer with a bar that says so', async () => {
    open();
    await flush();
    await menuOf('id2');
    root.querySelector<HTMLButtonElement>('button[data-edit]')!.click();
    await flush();
    expect(field().value).toBe('tin của mẹ');
    expect(text(root.querySelector('[data-editbar]')!)).toContain('Đang chỉnh sửa');
    expect(root.querySelector('.menu')).toBeNull();
  });

  it('sends the change to that message and not a new one, then leaves edit mode', async () => {
    const h = open();
    await flush();
    await menuOf('id2');
    root.querySelector<HTMLButtonElement>('button[data-edit]')!.click();
    await flush();
    type('  tin đã sửa  ');
    submit();
    await flush();
    expect(h.api.edit).toHaveBeenCalledWith(C1, 'id2', 'tin đã sửa');
    expect(h.api.send).not.toHaveBeenCalled();
    expect(root.querySelector('[data-editbar]')).toBeNull();
    expect(field().value).toBe('');
  });

  it('does not send an empty change', async () => {
    const h = open();
    await flush();
    await menuOf('id2');
    root.querySelector<HTMLButtonElement>('button[data-edit]')!.click();
    await flush();
    type('   ');
    submit();
    await flush();
    expect(h.api.edit).not.toHaveBeenCalled();
    expect(root.querySelector('[data-editbar]')).not.toBeNull();
  });

  it('keeps what was typed and says why when the server refuses', async () => {
    open({ edit: vi.fn(async () => fail('message_too_long', 400)) });
    await flush();
    await menuOf('id2');
    root.querySelector<HTMLButtonElement>('button[data-edit]')!.click();
    await flush();
    type('quá dài');
    submit();
    await flush();
    expect(root.querySelector('[role="alert"]')).not.toBeNull();
    expect(field().value).toBe('quá dài');
    expect(root.querySelector('[data-editbar]')).not.toBeNull();
  });

  it('cancels with the close button and restores an empty composer', async () => {
    const h = open();
    await flush();
    await menuOf('id2');
    root.querySelector<HTMLButtonElement>('button[data-edit]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('[data-cancel-edit]')!.click();
    await flush();
    expect(root.querySelector('[data-editbar]')).toBeNull();
    expect(field().value).toBe('');
    expect(h.api.edit).not.toHaveBeenCalled();
  });

  it('leaves edit mode when the message is taken back meanwhile', async () => {
    const h = open();
    await flush();
    await menuOf('id2');
    root.querySelector<HTMLButtonElement>('button[data-edit]')!.click();
    await flush();
    h.handlers().event(C1, recalled(base[1]!, 3));
    await flush();
    expect(root.querySelector('[data-editbar]')).toBeNull();
    expect(field().value).toBe('');
  });

  it('leaves a reply behind when you start editing', async () => {
    open();
    await flush();
    await menuOf('id1');
    root.querySelector<HTMLButtonElement>('button[data-reply]')!.click();
    await flush();
    expect(root.querySelector('[data-replybar]')).not.toBeNull();
    await menuOf('id2');
    root.querySelector<HTMLButtonElement>('button[data-edit]')!.click();
    await flush();
    expect(root.querySelector('[data-replybar]')).toBeNull();
    expect(root.querySelector('[data-editbar]')).not.toBeNull();
  });

  it('marks an edited message and shows the new words that arrive live', async () => {
    const h = open();
    await flush();
    expect(text(row('id1'))).not.toContain('đã chỉnh sửa');
    h.handlers().event(C1, { seq: 3, kind: 'updated', message: { ...base[0]!, body: 'đã sửa rồi', editedAt: at(11) } });
    await flush();
    expect(text(row('id1'))).toContain('đã sửa rồi');
    expect(text(row('id1'))).toContain('đã chỉnh sửa');
  });

  it('does not mark a message taken back as edited', async () => {
    open({}, [{ ...base[0]!, deleted: true, body: '', editedAt: at(11) }, base[1]!]);
    await flush();
    expect(text(row('id1'))).not.toContain('đã chỉnh sửa');
  });

  it('asks for the pins again when a pinned message is edited', async () => {
    const h = open({ pins: vi.fn(async () => ok({ pins: [base[0]!] })) });
    await flush();
    const calls = h.api.pins.mock.calls.length;
    h.handlers().event(C1, { seq: 3, kind: 'updated', message: { ...base[0]!, body: 'mới', editedAt: at(11) } });
    await flush();
    expect(h.api.pins.mock.calls.length).toBe(calls + 1);
  });
});

describe('chat: opening the menu by right-click or by a long press', () => {
  const base = [msg(1, 'minh', 'tin của Minh'), msg(2, 'mom', 'tin của mẹ')];
  const open = (extra: Record<string, unknown> = {}, messages: ChatMessage[] = base) =>
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh', { lastSeq: 2 })], history: { [C1]: messages }, api: extra });
  const bubble = (id: string) => root.querySelector(`[data-msg="${id}"] .bub`)!;
  const pointer = (type: string, init: { pointerType?: string; clientX?: number; clientY?: number } = {}) =>
    Object.assign(new Event(type, { bubbles: true }), { pointerType: init.pointerType ?? 'touch', clientX: init.clientX ?? 100, clientY: init.clientY ?? 100 });
  const menuFor = () => root.querySelector('.menu');

  it('opens the menu of the message that was right-clicked, and keeps the browser\'s own menu away', async () => {
    open();
    await flush();
    const event = new MouseEvent('contextmenu', { bubbles: true, cancelable: true });
    bubble('id1').dispatchEvent(event);
    await flush();
    expect(event.defaultPrevented).toBe(true);
    expect(menuFor()).not.toBeNull();
    expect(root.querySelector('button[data-reply]')).not.toBeNull();
  });

  it('right-clicking again on the same message keeps the menu open, and on another message moves it', async () => {
    open();
    await flush();
    bubble('id1').dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true })); await flush();
    bubble('id1').dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true })); await flush();
    expect(root.querySelectorAll('.menu')).toHaveLength(1);
    expect(root.querySelector('button[data-recall]')).toBeNull();
    bubble('id2').dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true })); await flush();
    expect(root.querySelectorAll('.menu')).toHaveLength(1);
    expect(root.querySelector('button[data-recall]')).not.toBeNull();
  });

  it('leaves the browser\'s menu alone on a message that has none (taken back, or still on its way)', async () => {
    open({ send: vi.fn(async () => fail('network', 0)) }, [{ ...base[0]!, deleted: true, body: '' }, base[1]!]);
    await flush();
    const gone = new MouseEvent('contextmenu', { bubbles: true, cancelable: true });
    bubble('id1').dispatchEvent(gone);
    expect(gone.defaultPrevented).toBe(false);
    expect(menuFor()).toBeNull();
  });

  it('opens the menu after a finger is held on a message for 450 ms', async () => {
    const h = open();
    await flush();
    bubble('id1').dispatchEvent(pointer('pointerdown'));
    const timer = h.timers[h.timers.length - 1]!;
    expect(timer.ms).toBe(450);
    expect(menuFor()).toBeNull();
    timer.fn();
    await flush();
    expect(menuFor()).not.toBeNull();
  });

  it('does not open it when the finger is lifted early', async () => {
    const h = open();
    await flush();
    bubble('id1').dispatchEvent(pointer('pointerdown'));
    const timer = h.timers[h.timers.length - 1]!;
    bubble('id1').dispatchEvent(pointer('pointerup'));
    timer.fn();
    await flush();
    expect(menuFor()).toBeNull();
  });

  it('does not open it when the finger moves on: that is scrolling', async () => {
    const h = open();
    await flush();
    bubble('id1').dispatchEvent(pointer('pointerdown', { clientX: 100, clientY: 100 }));
    const timer = h.timers[h.timers.length - 1]!;
    bubble('id1').dispatchEvent(pointer('pointermove', { clientX: 100, clientY: 130 }));
    timer.fn();
    await flush();
    expect(menuFor()).toBeNull();
  });

  it('still opens it when the finger only drifts a little', async () => {
    const h = open();
    await flush();
    bubble('id1').dispatchEvent(pointer('pointerdown', { clientX: 100, clientY: 100 }));
    const timer = h.timers[h.timers.length - 1]!;
    bubble('id1').dispatchEvent(pointer('pointermove', { clientX: 103, clientY: 104 }));
    timer.fn();
    await flush();
    expect(menuFor()).not.toBeNull();
  });

  it('does not open it when the finger is cancelled or leaves the message', async () => {
    const h = open();
    await flush();
    for (const end of ['pointercancel', 'pointerleave']) {
      bubble('id1').dispatchEvent(pointer('pointerdown'));
      const timer = h.timers[h.timers.length - 1]!;
      bubble('id1').dispatchEvent(pointer(end));
      timer.fn();
      await flush();
      expect(menuFor()).toBeNull();
    }
  });

  it('forgets an earlier press when a new finger goes down on another message', async () => {
    const h = open();
    await flush();
    bubble('id1').dispatchEvent(pointer('pointerdown'));
    const first = h.timers[h.timers.length - 1]!;
    bubble('id2').dispatchEvent(pointer('pointerdown'));
    first.fn();
    await flush();
    expect(menuFor()).toBeNull();
  });

  it('does not start a long press for a mouse', async () => {
    const h = open();
    await flush();
    const before = h.timers.length;
    bubble('id1').dispatchEvent(pointer('pointerdown', { pointerType: 'mouse' }));
    expect(h.timers.length).toBe(before);
  });

  it('does not start a long press on a message with no menu', async () => {
    const h = open({}, [{ ...base[0]!, deleted: true, body: '' }, base[1]!]);
    await flush();
    const before = h.timers.length;
    bubble('id1').dispatchEvent(pointer('pointerdown'));
    expect(h.timers.length).toBe(before);
  });

  it('keeps the button for keyboard and screen reader use', async () => {
    open();
    await flush();
    const button = root.querySelector<HTMLButtonElement>('[data-msg="id1"] button[data-menu]')!;
    expect(button.getAttribute('aria-label')).toBe('Tùy chọn tin nhắn');
    expect(button.hidden).toBe(false);   // the stylesheet may hide it from the eye, never from the page
    button.click();
    await flush();
    expect(menuFor()).not.toBeNull();
  });
});

describe('chat: where the menu opens', () => {
  const base = [msg(1, 'minh', 'tin của Minh')];
  it('is placed at the pointer, through the style object (a style attribute is refused by the page policy)', async () => {
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh', { lastSeq: 1 })], history: { [C1]: base } });
    await flush();
    root.querySelector('[data-msg="id1"] .bub')!.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 300.4, clientY: 150 }));
    await flush();
    const menu = root.querySelector<HTMLElement>('.menu')!;
    expect(menu.style.getPropertyValue('--x')).toBe('300px');
    expect(menu.style.getPropertyValue('--y')).toBe('150px');
  });

  it('closes when the dimmed area behind it is pressed, and on Escape', async () => {
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh', { lastSeq: 1 })], history: { [C1]: base } });
    await flush();
    const open = async () => { root.querySelector('[data-msg="id1"] .bub')!.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true })); await flush(); };
    await open();
    root.querySelector<HTMLElement>('[data-scrim]')!.click(); await flush();
    expect(root.querySelector('.menu')).toBeNull();
    await open();
    root.querySelector('.menu')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })); await flush();
    expect(root.querySelector('.menu')).toBeNull();
  });
});

describe('chat: runs of messages', () => {
  it('shows the time on the last message of a run only, and keeps it for a screen reader on the others', async () => {
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh', { lastSeq: 2 })], history: { [C1]: [msg(1, 'minh', 'một'), msg(2, 'minh', 'hai')] } });
    await flush();
    expect(root.querySelector('[data-msg="id1"] .ts')).toBeNull();
    expect(root.querySelector('[data-msg="id1"] .sr-only')!.textContent).toMatch(/\d{2}:\d{2}/);
    expect(root.querySelector('[data-msg="id2"] .ts')!.textContent).toMatch(/\d{2}:\d{2}/);
  });
});

describe('chat: the list of conversations', () => {
  const several = [chat(C1, 'Đào Thị Lan', { lastSeq: 0 }), chat(C2, 'Gia đình', { lastSeq: 0 })];
  const type = (value: string) => { const f = root.querySelector<HTMLInputElement>('input[name="filter"]')!; f.value = value; f.dispatchEvent(new Event('input', { bubbles: true })); };
  const names = () => [...root.querySelectorAll('.item .nm')].map((n) => n.textContent);

  it('filters by name as you type, ignoring accents and capitals, and brings everything back when cleared', async () => {
    start({ chats: several });
    await flush();
    expect(names()).toEqual(['Đào Thị Lan', 'Gia đình']);
    type('DAO'); await flush();
    expect(names()).toEqual(['Đào Thị Lan']);
    type('gia dinh'); await flush();
    expect(names()).toEqual(['Gia đình']);
    type(''); await flush();
    expect(names()).toHaveLength(2);
  });

  it('says so when nothing matches, and Escape clears the search', async () => {
    start({ chats: several });
    await flush();
    type('zzz'); await flush();
    expect(root.querySelector('[data-no-match]')).not.toBeNull();
    root.querySelector('input[name="filter"]')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })); await flush();
    expect(root.querySelector('[data-no-match]')).toBeNull();
    expect(names()).toHaveLength(2);
  });

  it('has no search when there is nothing to search', async () => {
    start({ chats: [] });
    await flush();
    expect(root.querySelector('input[name="filter"]')).toBeNull();
  });

  it('goes home from the Gom button: the open conversation closes and the address is the start', async () => {
    const h = start({ path: `/c/${C1}`, chats: several });
    await flush();
    expect(root.querySelector('.chat')!.getAttribute('data-pane')).toBe('thread');
    root.querySelector<HTMLAnchorElement>('a.brand')!.click();
    await flush();
    expect(root.querySelector('.chat')!.getAttribute('data-pane')).toBe('list');
    expect(h.nav.push).toHaveBeenLastCalledWith('/');
  });

  it('does not add a history entry when the Gom button is pressed at the start already', async () => {
    const h = start({ chats: several });
    await flush();
    root.querySelector<HTMLAnchorElement>('a.brand')!.click();
    await flush();
    expect(h.nav.push).not.toHaveBeenCalled();
  });

  it('shows a welcome in the empty right side instead of nothing', async () => {
    start({ chats: several });
    await flush();
    expect(text(root.querySelector('.thread-pane.none')!)).toContain('Chọn một cuộc trò chuyện');
    expect(root.querySelector('.thread-pane.none svg')).not.toBeNull();
  });
});

describe('chat: avatars and the header', () => {
  it('has no theme button in the list header any more (it lives in settings)', async () => {
    start({ chats: [chat(C1, 'Minh', { lastSeq: 0 })] });
    await flush();
    expect(root.querySelectorAll('.list-pane header button')).toHaveLength(0);
    expect(root.querySelector('.list-pane header a[href="/settings"]')).not.toBeNull();
  });

  it('colours the avatar beside a message in a group by the sender\'s name', async () => {
    const G = '55555555-5555-4555-8555-555555555555';
    const hung = { ...msg(1, 'minh', 'xin chào', at(10, 1), G), sender: { id: 'u-hung', displayName: 'Hùng' } };
    expect(avatarTone('Hùng')).not.toBe(1);
    start({ path: `/c/${G}`, chats: [{ id: G, kind: 'group', title: 'Nhóm', peer: null, lastSeq: 1, lastReadSeq: 1, unread: 0, lastMessage: null, memberCount: 3 }], history: { [G]: [hung] } });
    await flush();
    expect(root.querySelector('[data-msg="id1"] .av.sm')!.classList.contains(`a${avatarTone('Hùng')}`)).toBe(true);
  });

  it('gives a person the same colour in the list and in the thread', async () => {
    const name = 'Hùng';   // not tone 1, so a page that never varies its avatars fails here
    expect(avatarTone(name)).not.toBe(1);
    start({ path: `/c/${C1}`, chats: [chat(C1, name, { lastSeq: 0 })] });
    await flush();
    expect(root.querySelector('.item .av')!.classList.contains(`a${avatarTone(name)}`)).toBe(true);
    expect(root.querySelector('.th-head .av')!.classList.contains(`a${avatarTone(name)}`)).toBe(true);
  });
});

describe('chat: the desktop shell (rail, gear menu, info panel)', () => {
  const G = '33333333-3333-4333-8333-333333333333';
  const group = (): Conversation => ({ id: G, kind: 'group', title: 'Nhóm nhà', peer: null, lastSeq: 1, lastReadSeq: 1, unread: 0, lastMessage: null, memberCount: 2 });
  const people = [
    { id: 'u-mom', displayName: 'Mẹ', isMe: true, isFriend: false, gomId: 'mom', request: 'none' as const, role: 'leader' as const },
    { id: 'u-a', displayName: 'Anh', isMe: false, isFriend: true, gomId: 'anh', request: 'none' as const, role: 'member' as const },
  ];
  beforeEach(() => { try { localStorage.removeItem('gom-info'); } catch { /* nothing to clear */ } });

  it('opens the gear menu from the rail, closes it with Escape or a click outside, and offers sign-out', async () => {
    const h = start({ chats: [chat(C1, 'Minh')], api: { signOut: vi.fn(async () => ok(undefined)) } });
    await flush();
    expect(root.querySelector('.gearmenu')).toBeNull();
    root.querySelector<HTMLButtonElement>('[data-gear]')!.click();
    await flush();
    expect(root.querySelector('.gearmenu a[href="/settings"]')).not.toBeNull();
    root.querySelector('.gearmenu')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await flush();
    expect(root.querySelector('.gearmenu')).toBeNull();
    root.querySelector<HTMLButtonElement>('[data-gear]')!.click();
    await flush();
    root.querySelector<HTMLElement>('[data-gear-scrim]')!.click();
    await flush();
    expect(root.querySelector('.gearmenu')).toBeNull();
    root.querySelector<HTMLButtonElement>('[data-gear]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('[data-signout]')!.click();
    await flush();
    expect(h.api.signOut).toHaveBeenCalledOnce();
    expect(h.go).toHaveBeenCalledWith('/sign-in');
  });

  it('shows the owner link in the gear menu only to the owner', async () => {
    start({ chats: [chat(C1, 'Minh')] });
    await flush();
    root.querySelector<HTMLButtonElement>('[data-gear]')!.click();
    await flush();
    expect(root.querySelector('.gearmenu a[href="/owner"]')).toBeNull();
  });

  it('opens an info panel with the pins and the members of a group, and remembers that it is open', async () => {
    const pinned = msg(1, 'mom', 'ghim này', at(10), G);
    const h = start({
      path: `/c/${G}`, chats: [group()], history: { [G]: [pinned] },
      api: { groupMembers: vi.fn(async () => ok(people)), pins: vi.fn(async () => ok({ pins: [pinned] })) },
    });
    await flush();
    expect(root.querySelector('[data-info-panel]')).toBeNull();
    root.querySelector<HTMLButtonElement>('[data-info]')!.click();
    await flush();
    const panel = root.querySelector('[data-info-panel]')!;
    expect(text(panel)).toContain('Nhóm nhà');
    expect(panel.querySelectorAll('[data-info-member]')).toHaveLength(2);
    expect(panel.querySelector('[data-info-pin]')).not.toBeNull();
    expect(h.api.groupMembers).toHaveBeenCalledWith(G);
    expect(localStorage.getItem('gom-info')).toBe('1');
    root.querySelector<HTMLButtonElement>('[data-info]')!.click();
    await flush();
    expect(root.querySelector('[data-info-panel]')).toBeNull();
    expect(localStorage.getItem('gom-info')).toBe('0');
  });

  it('starts with the panel open when it was left open, and asks for the members only for a group', async () => {
    localStorage.setItem('gom-info', '1');
    const h = start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh')], history: { [C1]: [msg(1, 'minh')] }, api: { groupMembers: vi.fn(async () => ok(people)) } });
    await flush();
    expect(root.querySelector('[data-info-panel]')).not.toBeNull();
    expect(h.api.groupMembers).not.toHaveBeenCalled();
    expect(root.querySelector('[data-info-member]')).toBeNull();
  });
});

describe('chat: pictures and files', () => {
  const photo = (name = 'a.jpg') => new File([new Uint8Array([1, 2, 3])], name, { type: 'image/jpeg' });
  const att = (id: string, kind: 'image' | 'file' = 'image', name = 'a.webp'): Attachment => ({ id, kind, name, size: 2048, width: 40, height: 30 });
  const open = (extra: Record<string, unknown> = {}, history: ChatMessage[] = []) =>
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh', { lastSeq: history.length })], history: { [C1]: history }, api: extra });
  const choose = async (files: File[]) => {
    const input = root.querySelector<HTMLInputElement>('input[name="files"]')!;
    Object.defineProperty(input, 'files', { value: files, configurable: true });
    input.dispatchEvent(new Event('change', { bubbles: true }));
    await flush();
  };
  const trayItems = () => [...root.querySelectorAll<HTMLElement>('[data-tray-item]')];

  it('uploads what was chosen, shows it in the tray, and sends the message with the uploads, in the order chosen', async () => {
    const upload = vi.fn(async (_c: string, file: { name?: string }) => ok(att(`id-${file.name}`, 'image', file.name)));
    const send = vi.fn(async (c: string, clientId: string, body: string) => ok({ ...msg(1, 'mom', body, at(10), c), clientId }));
    const h = open({ upload, send });
    await flush();
    await choose([photo('1.jpg'), photo('2.jpg')]);
    expect(upload).toHaveBeenCalledTimes(2);
    expect(trayItems()).toHaveLength(2);
    expect(trayItems()[0]!.querySelector('img')!.getAttribute('src')).toBe('/api/attachments/id-1.jpg/thumb');

    compose('xem này');
    submitForm();
    await flush();
    expect(send).toHaveBeenCalledWith(C1, 'new-1', 'xem này', undefined, ['id-1.jpg', 'id-2.jpg']);
    expect(trayItems()).toHaveLength(0);
    expect(h.api.upload).toHaveBeenCalledTimes(2);
  });

  it('sends only pictures with no words', async () => {
    const send = vi.fn(async (c: string, clientId: string, body: string) => ok({ ...msg(1, 'mom', body, at(10), c), clientId }));
    open({ upload: vi.fn(async () => ok(att('p1'))), send });
    await flush();
    await choose([photo()]);
    submitForm();
    await flush();
    expect(send).toHaveBeenCalledWith(C1, 'new-1', '', undefined, ['p1']);
  });

  it('does not send while a file is still going up, and the send button says so', async () => {
    const finish: Array<(v: unknown) => void> = [];
    const send = vi.fn();
    open({ upload: vi.fn(() => new Promise((resolve) => { finish.push(resolve); })), send });
    await flush();
    await choose([photo()]);
    expect(root.querySelector<HTMLButtonElement>('.composer .send')!.disabled).toBe(true);
    expect(root.querySelector('[data-tray-item] progress')).not.toBeNull();
    compose('xin chào');
    submitForm();
    await flush();
    expect(send).not.toHaveBeenCalled();
    finish[0]!(ok(att('p1')));
    await flush();
    expect(root.querySelector<HTMLButtonElement>('.composer .send')!.disabled).toBe(false);
  });

  it('shows why an upload failed in words and lets the person try again or remove it', async () => {
    const upload = vi.fn()
      .mockResolvedValueOnce(fail('too_large', 413))
      .mockResolvedValueOnce(ok(att('p1')));
    open({ upload });
    await flush();
    await choose([photo()]);
    expect(text(root.querySelector('[data-tray-item] [role="alert"]')!)).toContain('Tệp quá lớn');
    expect(root.querySelector<HTMLButtonElement>('.composer .send')!.disabled).toBe(true);
    root.querySelector<HTMLButtonElement>('[data-tray-retry]')!.click();
    await flush();
    expect(upload).toHaveBeenCalledTimes(2);
    expect(root.querySelector('[data-tray-item] img')).not.toBeNull();
    root.querySelector<HTMLButtonElement>('[data-tray-remove]')!.click();
    await flush();
    expect(trayItems()).toHaveLength(0);
  });

  it('takes at most ten and says so', async () => {
    const upload = vi.fn(async (_c: string, f: { name?: string }) => ok(att(`id-${f.name}`)));
    open({ upload });
    await flush();
    await choose(Array.from({ length: 12 }, (_, i) => photo(`${i}.jpg`)));
    expect(trayItems()).toHaveLength(10);
    expect(upload).toHaveBeenCalledTimes(10);
    expect(root.textContent).toContain('tối đa 10 tệp');
  });

  it('goes up two at a time', async () => {
    const finish: Array<(v: unknown) => void> = [];
    const upload = vi.fn(() => new Promise((resolve) => { finish.push(resolve); }));
    open({ upload });
    await flush();
    await choose([photo('1'), photo('2'), photo('3')]);
    expect(upload).toHaveBeenCalledTimes(2);
    finish[0]!(ok(att('a')));
    await flush();
    expect(upload).toHaveBeenCalledTimes(3);
  });

  it('takes pictures pasted into the box, and leaves pasted text alone', async () => {
    const upload = vi.fn(async () => ok(att('p1')));
    open({ upload });
    await flush();
    const area = root.querySelector('textarea')!;
    const textPaste = new Event('paste', { bubbles: true, cancelable: true }) as unknown as ClipboardEvent;
    Object.defineProperty(textPaste, 'clipboardData', { value: { files: [] } });
    area.dispatchEvent(textPaste);
    expect(textPaste.defaultPrevented).toBe(false);
    const filePaste = new Event('paste', { bubbles: true, cancelable: true }) as unknown as ClipboardEvent;
    Object.defineProperty(filePaste, 'clipboardData', { value: { files: [photo()] } });
    area.dispatchEvent(filePaste);
    await flush();
    expect(filePaste.defaultPrevented).toBe(true);
    expect(upload).toHaveBeenCalledOnce();
  });

  it('draws the pictures of a message as thumbnails and its files as cards, with no empty bubble of words', async () => {
    const m: ChatMessage = { ...msg(1, 'minh', '', at(10)), attachments: [att('p1'), att('p2'), att('f1', 'file', 'ghi chú.txt')] };
    open({}, [m]);
    await flush();
    const bubble = root.querySelector('[data-msg="id1"] .bub')!;
    expect(bubble.querySelectorAll('.pic img')).toHaveLength(2);
    expect(bubble.querySelector('.pic img')!.getAttribute('src')).toBe('/api/attachments/p1/thumb');
    expect(bubble.querySelector('.pic img')!.getAttribute('width')).toBe('40');
    const card = bubble.querySelector<HTMLAnchorElement>('.filecard')!;
    expect(card.getAttribute('href')).toBe('/api/attachments/f1');
    expect(card.getAttribute('download')).toBe('ghi chú.txt');
    expect(text(card)).toContain('2 KB');
    expect(bubble.querySelector('.txt')).toBeNull();
  });

  it('opens a picture large, moves between the pictures of the message, and closes with Escape', async () => {
    const m: ChatMessage = { ...msg(1, 'minh', 'ảnh', at(10)), attachments: [att('p1'), att('p2'), att('p3')] };
    open({}, [m]);
    await flush();
    root.querySelector<HTMLButtonElement>('[data-pic="p2"]')!.click();
    await flush();
    const viewer = () => root.querySelector('[data-viewer]');
    expect(viewer()!.querySelector('.vimg')!.getAttribute('src')).toBe('/api/attachments/p2');
    expect(text(viewer()!.querySelector('.viewer-bar span')!)).toBe('2 / 3');
    root.querySelector<HTMLButtonElement>('[data-viewer-next]')!.click();
    await flush();
    expect(viewer()!.querySelector('.vimg')!.getAttribute('src')).toBe('/api/attachments/p3');
    root.querySelector<HTMLButtonElement>('[data-viewer-next]')!.click();
    await flush();
    expect(viewer()!.querySelector('.vimg')!.getAttribute('src')).toBe('/api/attachments/p1');   // round again
    viewer()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true }));
    await flush();
    expect(viewer()!.querySelector('.vimg')!.getAttribute('src')).toBe('/api/attachments/p3');
    viewer()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await flush();
    expect(viewer()).toBeNull();
  });

  it('has no arrows for a single picture and closes with the button', async () => {
    open({}, [{ ...msg(1, 'minh', '', at(10)), attachments: [att('p1')] }]);
    await flush();
    root.querySelector<HTMLButtonElement>('[data-pic="p1"]')!.click();
    await flush();
    expect(root.querySelector('[data-viewer-next]')).toBeNull();
    root.querySelector<HTMLButtonElement>('[data-viewer-close]')!.click();
    await flush();
    expect(root.querySelector('[data-viewer]')).toBeNull();
  });

  it('shows the pictures it has sent while the message is still going, and keeps them in the outbox across a reload', async () => {
    const send = vi.fn(() => new Promise(() => undefined));
    const h = open({ upload: vi.fn(async () => ok(att('p1'))), send });
    await flush();
    await choose([photo()]);
    submitForm();
    await flush();
    expect(root.querySelector('[data-msg="new-1"] .pic img')).not.toBeNull();
    expect(h.outbox.items[0]!.attachments![0]!.id).toBe('p1');
  });

  it('puts back an outbox message with its pictures and sends the same upload ids', async () => {
    const send = vi.fn(async (c: string, clientId: string, body: string) => ok({ ...msg(1, 'mom', body, at(10), c), clientId }));
    start({
      path: `/c/${C1}`, chats: [chat(C1, 'Minh')], history: { [C1]: [] }, api: { send },
      outbox: [{ conversation: C1, clientId: 'old-1', body: '', createdAt: at(9), attachments: [att('p9')] }],
    });
    await flush();
    expect(send).toHaveBeenCalledWith(C1, 'old-1', '', undefined, ['p9']);
  });

  it('lists the pictures and files seen so far in the side panel', async () => {
    localStorage.setItem('gom-info', '1');
    open({}, [{ ...msg(1, 'minh', '', at(10)), attachments: [att('p1'), att('f1', 'file', 'bảng.xlsx')] }]);
    await flush();
    expect(root.querySelector('[data-info-media] [data-info-pic="p1"]')).not.toBeNull();
    expect(text(root.querySelector('[data-info-files]')!)).toContain('bảng.xlsx');
    localStorage.removeItem('gom-info');
  });
});

describe('chat: addresses in messages', () => {
  const open = (body: string) => start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh', { lastSeq: 1 })], history: { [C1]: [msg(1, 'minh', body)] } });

  it('makes an address a link that opens in a new tab without telling that site who sent you', async () => {
    open('xem https://example.com/a?b=1, nhé');
    await flush();
    const link = root.querySelector<HTMLAnchorElement>('[data-msg="id1"] a.ext')!;
    expect(link.getAttribute('href')).toBe('https://example.com/a?b=1');
    expect(link.textContent).toBe('https://example.com/a?b=1');
    expect(link.getAttribute('target')).toBe('_blank');
    expect(link.getAttribute('rel')).toBe('noopener noreferrer nofollow');
    expect(text(root.querySelector('[data-msg="id1"] .txt')!)).toBe('xem https://example.com/a?b=1, nhé');
  });

  it('leaves a javascript address and an address with a password in front as plain words', async () => {
    open('javascript:alert(1) https://google.com@evil.example/');
    await flush();
    expect(root.querySelector('[data-msg="id1"] a')).toBeNull();
  });

  it('does not turn what is written in the box or a quoted snippet into a link', async () => {
    open('chỉ là chữ');
    await flush();
    compose('https://example.com');
    expect(root.querySelector('textarea')!.value).toBe('https://example.com');
    expect(root.querySelector('.composer a')).toBeNull();
  });

  it('keeps the message menu away from an address on a right click, so the browser can offer to copy it', async () => {
    open('https://example.com/x');
    await flush();
    const on = new MouseEvent('contextmenu', { bubbles: true, cancelable: true });
    root.querySelector('[data-msg="id1"] a.ext')!.dispatchEvent(on);
    expect(on.defaultPrevented).toBe(false);
    expect(root.querySelector('.menu')).toBeNull();
    const off = new MouseEvent('contextmenu', { bubbles: true, cancelable: true });
    root.querySelector('[data-msg="id1"] .bub')!.dispatchEvent(off);
    expect(off.defaultPrevented).toBe(true);
  });
});

describe('chat: the card under a message for its address', () => {
  const preview = { url: 'https://example.com/a', title: 'Chuyến đi Đà Lạt', description: 'Ảnh và kế hoạch', site: 'Gom Blog', imageId: 'img1' };
  const open = (m: ChatMessage, extra: Record<string, unknown> = {}) =>
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh', { lastSeq: 1 })], history: { [C1]: [m] }, api: extra });

  it('draws the site, the title, the words and the small picture, as a link that opens in a new tab', async () => {
    open({ ...msg(1, 'minh', 'xem https://example.com/a'), preview });
    await flush();
    const card = root.querySelector('[data-preview]')!;
    const link = card.querySelector<HTMLAnchorElement>('a.lc-main')!;
    expect(link.getAttribute('href')).toBe('https://example.com/a');
    expect(link.getAttribute('target')).toBe('_blank');
    expect(link.getAttribute('rel')).toBe('noopener noreferrer nofollow');
    expect(card.querySelector('img')!.getAttribute('src')).toBe('/api/attachments/img1/thumb');
    expect(text(card)).toContain('Gom Blog');
    expect(text(card)).toContain('Chuyến đi Đà Lạt');
    expect(text(card)).toContain('Ảnh và kế hoạch');
  });

  it('shows no card for an address the page could not make a link of, or when there is nothing to say', async () => {
    open({ ...msg(1, 'minh', 'x'), preview: { ...preview, url: 'javascript:alert(1)' } });
    await flush();
    expect(root.querySelector('[data-preview]')).toBeNull();
    root = document.createElement('div'); document.body.replaceChildren(root);
    open({ ...msg(1, 'minh', 'x'), preview: { ...preview, title: null, description: null } });
    await flush();
    expect(root.querySelector('[data-preview]')).toBeNull();
  });

  it('has no picture area when the page had no picture', async () => {
    open({ ...msg(1, 'minh', 'x'), preview: { ...preview, imageId: null } });
    await flush();
    expect(root.querySelector('[data-preview] img')).toBeNull();
  });

  it('lets only the writer take the card away, and takes it off at once', async () => {
    open({ ...msg(1, 'minh', 'x'), preview });
    await flush();
    expect(root.querySelector('[data-preview-remove]')).toBeNull();
    root = document.createElement('div'); document.body.replaceChildren(root);
    const removePreview = vi.fn(async () => ok(undefined));
    open({ ...msg(1, 'mom', 'x'), preview }, { removePreview });
    await flush();
    root.querySelector<HTMLButtonElement>('[data-preview-remove]')!.click();
    await flush();
    expect(removePreview).toHaveBeenCalledWith(C1, 'id1');
    expect(root.querySelector('[data-preview]')).toBeNull();
  });

  it('puts the card back and says why when taking it away fails', async () => {
    open({ ...msg(1, 'mom', 'x'), preview }, { removePreview: vi.fn(async () => fail('server', 500)) });
    await flush();
    root.querySelector<HTMLButtonElement>('[data-preview-remove]')!.click();
    await flush();
    expect(root.querySelector('[data-preview]')).not.toBeNull();
    expect(root.querySelector('[role="alert"]')).not.toBeNull();
  });

  it('shows a card that arrives later in an update of the same message', async () => {
    const h = open(msg(1, 'minh', 'xem https://example.com/a'));
    await flush();
    expect(root.querySelector('[data-preview]')).toBeNull();
    h.handlers().event(C1, { seq: 2, kind: 'updated', message: { ...msg(1, 'minh', 'xem https://example.com/a'), preview } });
    await flush();
    expect(root.querySelector('[data-preview]')).not.toBeNull();
  });
});

describe('chat: notices in a group', () => {
  const G = '33333333-3333-4333-8333-333333333333';
  const group = (extra: Partial<Conversation> = {}): Conversation => ({ id: G, kind: 'group', title: 'Nhóm', peer: null, lastSeq: 2, lastReadSeq: 2, unread: 0, lastMessage: { seq: 1, body: 'tin cuối', createdAt: at(10), sender: { id: 'u-minh', displayName: 'Minh' } }, memberCount: 3, ...extra });
  const notice = (seq: number, type: 'added' | 'left' | 'renamed', by: 'mom' | 'minh' = 'minh', target?: { id: string; displayName: string }, title?: string): ChatMessage =>
    ({ ...msg(seq, by, '', at(10, seq), G), notice: { type, target: target ?? null, title: title ?? null } });
  const open = (history: ChatMessage[]) => start({ path: `/c/${G}`, chats: [group()], history: { [G]: history } });

  it('draws a notice as a line between the messages, in words, not as a bubble', async () => {
    open([msg(1, 'minh', 'tin', at(10), G), notice(2, 'added', 'minh', { id: 'u-bao', displayName: 'Bảo' })]);
    await flush();
    const line = root.querySelector('[data-notice]')!;
    expect(text(line)).toBe('Minh đã thêm Bảo vào nhóm');
    expect(root.querySelectorAll('[data-msg]')).toHaveLength(1);   // the notice is not a bubble
    expect(root.querySelector('[data-notice] .tools, [data-notice] .menu-btn')).toBeNull();
  });

  it('says "Bạn" and "bạn" when it is about you', async () => {
    open([notice(1, 'added', 'mom', { id: 'u-mom', displayName: 'Mẹ' }), notice(2, 'renamed', 'mom', undefined, 'Nhà mình')]);
    await flush();
    expect([...root.querySelectorAll('[data-notice]')].map((n) => text(n))).toEqual(['Bạn đã thêm bạn vào nhóm', 'Bạn đã đổi tên nhóm thành "Nhà mình"']);
  });

  it('puts no tick or seen line on a notice of yours, even when it is the newest thing you did', async () => {
    const h = start({
      path: `/c/${G}`, chats: [group()], history: { [G]: [msg(1, 'mom', 'tin', at(10), G), notice(2, 'renamed', 'mom', undefined, 'Tên mới')] },
      api: { reads: vi.fn(async () => ok({ enabled: true, reads: [{ userId: 'u-a', seq: 9 }] })) },
    });
    await flush();
    expect(root.querySelector('[data-notice] .tick')).toBeNull();
    expect(text(root.querySelector('.seen')!)).toBe('Đã xem bởi 1');
    expect(root.querySelector('[data-msg="id1"] .tick')).not.toBeNull();
    expect(h.api.reads).toHaveBeenCalled();
  });

  it('does not change the list preview, count as unread, or offer "new messages" when a notice arrives live', async () => {
    const h = start({ chats: [group()], history: {} });
    await flush();
    const before = text(rows()[0]!.querySelector('.pv')!);
    h.handlers().event(G, created(notice(3, 'left', 'minh')));
    await flush();
    expect(text(rows()[0]!.querySelector('.pv')!)).toBe(before);
    expect(rows()[0]!.querySelector('.badge')).toBeNull();
  });

  it('shows a notice that arrives live in the open thread without a "new messages" jump button', async () => {
    const h = open([msg(1, 'minh', 'tin', at(10), G)]);
    await flush();
    h.handlers().event(G, created(notice(2, 'left', 'minh')));
    await flush();
    expect(text(root.querySelector('[data-notice]')!)).toBe('Minh đã rời nhóm');
    expect(root.querySelector('[data-jump-bottom]')).toBeNull();
  });
});

describe('chat: a server without uploads', () => {
  const photo = new File([new Uint8Array([1, 2, 3])], 'a.jpg', { type: 'image/jpeg' });
  const open = (uploads: boolean | undefined, upload = vi.fn(async () => fail('uploads_disabled', 503))) => {
    const mine = uploads === undefined ? me : { ...me, uploads };
    const h = start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh')], history: { [C1]: [] }, api: { me: vi.fn(async () => ok(mine)), upload } });
    return { h, upload };
  };
  const drop = (files: File[]) => {
    const event = new Event('drop', { bubbles: true, cancelable: true });
    Object.defineProperty(event, 'dataTransfer', { value: { files, types: ['Files'] } });
    root.querySelector('.thread-pane')!.dispatchEvent(event);
    return event;
  };

  it('offers no attach button when the server says it takes no uploads', async () => {
    open(false);
    await flush();
    expect(root.querySelector('[data-attach]')).toBeNull();
    expect(root.querySelector('input[name="files"]')).toBeNull();
  });

  it('still offers it when the server takes uploads, or does not say (an older server)', async () => {
    open(true);
    await flush();
    expect(root.querySelector('[data-attach]')).not.toBeNull();
    document.body.replaceChildren(root);
    open(undefined);
    await flush();
    expect(root.querySelector('[data-attach]')).not.toBeNull();
  });

  it('does not upload a dropped file, and does not let the browser open it in place of the page', async () => {
    const { upload } = open(false);
    await flush();
    const event = drop([photo]);
    await flush();
    expect(upload).not.toHaveBeenCalled();
    expect(root.querySelector('[data-tray-item]')).toBeNull();
    expect(event.defaultPrevented).toBe(true);
  });

  it('uploads a dropped file when the server takes uploads', async () => {
    const upload = vi.fn(async () => ok({ id: 'a1', kind: 'image' as const, name: 'a.webp', size: 10, width: 4, height: 3 }));
    open(true, upload as never);
    await flush();
    drop([photo]);
    await flush();
    expect(upload).toHaveBeenCalledTimes(1);
  });
});

describe('chat: the account menu', () => {
  it('shows a door beside the sign-out entry, like the other entries have their icons', async () => {
    start({ path: '/', chats: [chat(C1, 'Minh')] });
    await flush();
    root.querySelector<HTMLButtonElement>('[data-gear]')!.click();
    await flush();
    const signOut = root.querySelector('[data-signout]')!;
    expect(signOut.querySelector('svg.i path')).not.toBeNull();
    expect(text(signOut)).toBe('Đăng xuất');
  });
});
