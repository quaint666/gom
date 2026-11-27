import { afterEach, beforeEach, describe, expect, it, onTestFinished, vi } from 'vitest';
import { avatarTone, clock } from '../Scripts/chat/model.js';
import type { Attachment, ChatEvent, ChatMessage, Conversation } from '../Scripts/api.js';
import type { Pusher, PushSupport } from '../Scripts/push.js';
import type { LiveControl, LiveHandlers } from '../Scripts/chat/live.js';
import { mountChat, type ChatDeps, type OutboxItem } from '../Scripts/pages/chat.js';
import { STICKER_PACKS, STICKERS } from '../Scripts/stickers.js';
import { vi as vietnamese } from '../Scripts/strings/vi.js';
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
  id, kind: 'direct', title: name, peer: { id: `u-${name.toLowerCase()}`, gomId: name.toLowerCase(), displayName: name }, lastSeq: 0, lastReadSeq: 0, unread: 0, lastMessage: null, ...extra,
});
const withLast = (m: ChatMessage, extra: Partial<Conversation> = {}) =>
  chat(m.conversationId, m.sender.displayName, { lastSeq: m.seq, lastMessage: { seq: m.seq, body: m.body, createdAt: m.createdAt, sender: m.sender }, ...extra });

interface Harness {
  api: ReturnType<typeof fakeApi>;
  handlers: () => LiveHandlers;
  live: { start: ReturnType<typeof vi.fn>; stop: ReturnType<typeof vi.fn>; sync: ReturnType<typeof vi.fn>; retryNow: ReturnType<typeof vi.fn> };
  nav: { path: string; push: ReturnType<typeof vi.fn>; replace: ReturnType<typeof vi.fn>; back: ReturnType<typeof vi.fn<() => void>>; pop: () => void; history: string[] };
  go: ReturnType<typeof vi.fn>;
  visible: { value: boolean };
  coarse: { value: boolean };
  timers: { ms: number; fn: () => void; live: boolean }[];
  outbox: { items: OutboxItem[]; saves: number };
  notes: ReturnType<typeof vi.fn>;
  /** The words kept for a reload, as "<gomId>:<conversation>" → words. */
  drafts: Map<string, string>;
  /** The page's answer to "would a reload lose something?", once it has handed it over. */
  busy: () => boolean;
}

function start(opts: { lastSeq?: Record<string, number>; outbox?: Harness['outbox']['items']; path?: string; chats?: Conversation[]; history?: Record<string, ChatMessage[]>; hasMore?: boolean; api?: Record<string, unknown>; pusher?: Pusher; prepareVideo?: ChatDeps['prepareVideo']; recordVoice?: ChatDeps['recordVoice']; drafts?: Map<string, string>; keyboardImages?: ChatDeps['keyboardImages']; calls?: ChatDeps['calls']; callsOn?: boolean } = {}): Harness {
  const history = opts.history ?? {};
  const api = fakeApi({
    me: vi.fn(async () => ok(opts.callsOn ? { ...me, calls: true } : me)),
    // A fresh copy each time, as a real server gives: the page changes what it is handed.
    conversations: vi.fn(async () => ok(structuredClone(opts.chats ?? []))),
    history: vi.fn(async (id: string) => ok({ messages: [...(history[id] ?? [])].reverse(), hasMore: opts.hasMore ?? false, lastSeq: opts.lastSeq?.[id] ?? Math.max(0, ...(history[id] ?? []).map((m) => m.seq)) })),
    ...(opts.api ?? {}),
  });
  let captured: LiveHandlers | null = null;
  const live = { start: vi.fn(), stop: vi.fn(), sync: vi.fn(), retryNow: vi.fn() };
  let popCallback: () => void = () => undefined;
  // Like the browser's history: the path is location.pathname (no #part), and going back fires the pop handler.
  const nav = {
    path: opts.path ?? '/',
    history: [opts.path ?? '/'],
    push: vi.fn((p: string) => { nav.history.push(p); nav.path = p.split('#')[0]!; }),
    replace: vi.fn((p: string) => { nav.history[nav.history.length - 1] = p; nav.path = p.split('#')[0]!; }),
    back: vi.fn(() => { nav.history.pop(); nav.path = (nav.history.at(-1) ?? '/').split('#')[0]!; popCallback(); }),
    pop: () => popCallback(),
  };
  const notes = vi.fn();
  const visible = { value: true };
  const coarse = { value: false };
  const go = vi.fn();
  let n = 0;
  const timers: Harness['timers'] = [];
  const outbox: Harness['outbox'] = { items: opts.outbox ?? [], saves: 0 };
  const drafts = opts.drafts ?? new Map<string, string>();
  let busy: () => boolean = () => { throw new Error('not handed over'); };
  const deps: ChatDeps = {
    drafts: (gomId) => ({
      load: (c) => drafts.get(`${gomId}:${c}`) ?? '',
      save: (c, words) => { if (words === '') drafts.delete(`${gomId}:${c}`); else drafts.set(`${gomId}:${c}`, words); },
    }),
    holdReload: (check) => { busy = check; },
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
    nav: { path: () => nav.path, push: nav.push, replace: nav.replace, back: nav.back, onPop: (cb) => { popCallback = cb; } },
    now: () => NOW,
    newId: () => `new-${++n}`,
    coarsePointer: () => coarse.value,
    visible: () => visible.value,
    clearNotifications: notes,
    ...(opts.pusher ? { pusher: opts.pusher } : {}),
    ...(opts.prepareVideo ? { prepareVideo: opts.prepareVideo } : {}),
    ...(opts.recordVoice ? { recordVoice: opts.recordVoice } : {}),
    ...(opts.keyboardImages ? { keyboardImages: opts.keyboardImages } : {}),
    ...(opts.calls ? { calls: opts.calls } : {}),
  };
  mountChat(root, api, go, deps);
  return { api, handlers: () => captured!, live, nav, go, visible, coarse, timers, outbox, notes, drafts, busy: () => busy() };
}

const rows = () => [...root.querySelectorAll<HTMLElement>('[data-chat]')];

/**
 * jsdom has no layout: here each element of the page is 10 px below the one before it, so where the thread was scrolled to can
 * be read back. Also counts scrollIntoView, which must not be used (it scrolls the page as well as the thread).
 */
function fakeLayout() {
  const original = Element.prototype.getBoundingClientRect;
  const proto = Element.prototype as unknown as { scrollIntoView?: unknown };
  const originalInto = proto.scrollIntoView;
  const top = (e: Element) => [...document.querySelectorAll('*')].indexOf(e) * 10;
  // As on screen: what is inside the thread moves up as far as the thread is scrolled.
  const shown = (e: Element) => { const box = e.parentElement?.closest<HTMLElement>('.msgs'); return top(e) - (box?.scrollTop ?? 0); };
  let intoView = 0;
  Element.prototype.getBoundingClientRect = function (this: Element) { const y = shown(this); return { top: y, bottom: y, left: 0, right: 0, width: 0, height: 0, x: 0, y, toJSON: () => ({}) } as DOMRect; };
  proto.scrollIntoView = () => { intoView++; };
  return {
    restore() { Element.prototype.getBoundingClientRect = original; proto.scrollIntoView = originalInto; },
    intoView: () => intoView,
    /** The message or divider now at the top of the thread, if one is. */
    landed(): Element | null {
      const box = root.querySelector<HTMLElement>('.msgs');
      if (box === null || box.scrollTop === 0) return null;
      return [...box.querySelectorAll('.newdiv, [data-msg]')].find((e) => top(e) - top(box) === box.scrollTop) ?? null;
    },
  };
}
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
    start({ chats: [withLast(msg(1, 'mom', 'Mẹ nhắn', at(12)), { peer: { id: 'u-minh', gomId: 'minh', displayName: 'Minh' }, title: 'Minh' })] });
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

  it('on a phone, slides the conversation out before the list shows, for the back button and the back gesture', async () => {
    vi.spyOn(window, 'matchMedia').mockImplementation((q: string) => ({ matches: q === '(max-width: 899.98px)', media: q } as MediaQueryList));
    const ends: Array<() => void> = [];
    const frames: Keyframe[][] = [];
    const animate = vi.fn((f: Keyframe[]) => { frames.push(f); return { finished: new Promise<void>((resolve) => { ends.push(resolve); }), cancel: vi.fn() }; });
    Object.defineProperty(HTMLElement.prototype, 'animate', { value: animate, configurable: true });
    onTestFinished(() => { delete (HTMLElement.prototype as { animate?: unknown }).animate; vi.restoreAllMocks(); });
    const h = start({ chats, history });
    await flush();
    rows()[0]!.click(); await flush();
    root.querySelector<HTMLElement>('.th-head .back')!.click(); await flush();
    expect(frames.at(-1)![1]).toEqual({ transform: 'translateX(40%)', opacity: 0 });
    expect(root.querySelector('.chat')!.getAttribute('data-pane')).toBe('thread');   // still sliding
    ends.at(-1)!(); await flush();
    expect(root.querySelector('.chat')!.getAttribute('data-pane')).toBe('list');
    rows()[0]!.click(); await flush();
    h.nav.path = '/'; h.nav.pop(); await flush();
    expect(root.querySelector('.chat')!.getAttribute('data-pane')).toBe('thread');
    ends.at(-1)!(); await flush();
    expect(root.querySelector('.chat')!.getAttribute('data-pane')).toBe('list');
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

  describe('when the server says to slow down', () => {
    const waits = (h: Harness) => h.timers.filter((t) => t.live);

    it('asks again about that conversation after a moment, from where this device is', async () => {
      const h = start({ path: `/c/${C1}`, chats, history: { [C1]: [msg(1, 'minh'), msg(2, 'minh')] } });
      await flush();
      h.live.sync.mockClear();
      h.handlers().slow(C1);
      expect(h.live.sync).not.toHaveBeenCalled();     // not at once
      expect(waits(h).at(-1)!.ms).toBe(1000);
      waits(h).at(-1)!.fn();
      expect(h.live.sync).toHaveBeenCalledTimes(1);
      expect(h.live.sync).toHaveBeenCalledWith(C1, 2);
    });

    it('waits once however many times it is told, and can be told again after asking', async () => {
      const h = start({ path: `/c/${C1}`, chats, history: { [C1]: [msg(1, 'minh')] } });
      await flush();
      h.live.sync.mockClear();
      const before = waits(h).length;
      h.handlers().slow(C1); h.handlers().slow(C1); h.handlers().slow(C1);
      expect(waits(h).length).toBe(before + 1);
      waits(h).at(-1)!.fn();
      expect(h.live.sync).toHaveBeenCalledTimes(1);
      h.handlers().slow(C1);
      expect(waits(h).length).toBe(before + 1);        // the first wait is spent; this is a new one
      waits(h).at(-1)!.fn();
      expect(h.live.sync).toHaveBeenCalledTimes(2);
    });

    it('does not ask for a conversation this device has not opened', async () => {
      const h = start({ path: `/c/${C1}`, chats, history: { [C1]: [msg(1, 'minh')] } });
      await flush();
      h.live.sync.mockClear();
      h.handlers().slow(C2);
      waits(h).at(-1)!.fn();
      expect(h.live.sync).not.toHaveBeenCalled();
    });
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

  it('leaves the field\'s height to the stylesheet, so it shrinks back once a long message is sent', async () => {
    open();
    await flush();
    compose('một\nhai\nba\nbốn');
    submitForm();
    await flush();
    expect(root.querySelector<HTMLTextAreaElement>('textarea')!.style.height).toBe('');
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

  it('keeps a message the server turned away as from an older page, to send after the reload, and does not retry it here', async () => {
    const send = vi.fn(async () => fail('update_required', 409));
    const h = open({ send });
    await flush();
    write('sau khi cập nhật');
    await flush();
    expect(pendingOf('new-1')!.getAttribute('data-state')).toBe('queued');
    expect(h.outbox.items.map((i) => i.body)).toEqual(['sau khi cập nhật']);
    expect(h.timers.filter((t) => t.live)).toHaveLength(0);
    expect(send).toHaveBeenCalledTimes(1);
  });

  it('does not say "reconnecting" once the server has turned the page away as too old', async () => {
    const h = open();
    await flush();
    h.handlers().state(false);
    expect(root.querySelector('.warn')).not.toBeNull();
    h.handlers().tooOld!();
    expect(root.querySelector('.warn')).toBeNull();
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
    const h = start({ chats: [withLast(msg(3, 'minh', 'mới', at(11))), withLast(old, { id: C2, title: 'Lan', peer: { id: 'u-lan', gomId: 'lan', displayName: 'Lan' } })] });
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
  const tick = (id: string) => { const box = root.querySelector<HTMLInputElement>(`[data-forward-pick="${id}"]`)!; box.checked = !box.checked; box.dispatchEvent(new Event('change', { bubbles: true })); };
  /** Ticks these conversations and presses "Chuyển tiếp". */
  const sendTo = async (...ids: string[]) => {
    for (const id of ids) tick(id);
    await flush();
    root.querySelector<HTMLButtonElement>('[data-forward-send]')!.click();
    await flush(); await flush();
  };

  it('lists your conversations to choose from, and shows the start of the message', async () => {
    open();
    await flush();
    await pick('id1');
    const panel = root.querySelector('[data-forward-panel]')!;
    expect(text(panel)).toContain('Tối nay ăn gì?');
    expect([...panel.querySelectorAll('[data-forward-pick]')].map((b) => b.getAttribute('data-forward-pick'))).toEqual([C1, C2]);
    expect(panel.tagName).toBe('DIALOG');
    expect(root.querySelector<HTMLButtonElement>('[data-forward-send]')!.disabled).toBe(true);   // nothing ticked yet
    expect(root.querySelector('.menu')).toBeNull();
  });

  it('leaves hidden conversations out of the choices', async () => {
    start({ path: `/c/${C1}`, chats: [...chats, chat('33333333-3333-4333-8333-333333333333', 'Bà', { hidden: true })], history: { [C1]: [msg(1, 'minh', 'Tối nay ăn gì?')] } });
    await flush();
    await pick('id1');
    expect([...root.querySelectorAll('[data-forward-pick]')].map((b) => b.getAttribute('data-forward-pick'))).toEqual([C1, C2]);
  });

  it('finds a hidden conversation by search, and one ticked stays in view when the search is cleared', async () => {
    const C3 = '33333333-3333-4333-8333-333333333333';
    start({ path: `/c/${C1}`, chats: [...chats, chat(C3, 'Bà', { hidden: true })], history: { [C1]: [msg(1, 'minh', 'Tối nay ăn gì?')] } });
    await flush();
    await pick('id1');
    fill(root, '[data-forward-search]', 'bà');
    await flush();
    expect([...root.querySelectorAll('[data-hidden-list] [data-forward-pick]')].map((b) => b.getAttribute('data-forward-pick'))).toEqual([C3]);
    expect(text(root.querySelector('[data-forward-panel] [data-hidden-label]')!)).toBe('Đã ẩn');
    tick(C3);
    fill(root, '[data-forward-search]', '');
    await flush();
    expect(root.querySelector<HTMLInputElement>(`[data-forward-pick="${C3}"]`)!.checked).toBe(true);
  });

  it('sends the words to the chosen conversation as a new message with no answer attached, and says so', async () => {
    const send = echo.mockClear();
    open({ send });
    await flush();
    await pick('id1');
    await sendTo(C2);
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
    await sendTo(C2);
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
    await sendTo(C2);
    expect(text(root.querySelector('.note')!)).toContain('bạn bè');
    expect(h.outbox.items).toHaveLength(0);
  });

  it('leaves no failed message behind in the conversation it was refused by', async () => {
    open({ send: vi.fn(async () => fail('not_friends', 403)) });
    await flush();
    await pick('id1');
    await sendTo(C2);
    root.querySelector<HTMLAnchorElement>(`a[data-chat="${C2}"]`)!.click();
    await flush();
    expect(root.querySelector('[data-state="failed"]')).toBeNull();
    expect(root.querySelector('[data-msg]')).toBeNull();
  });

  it('writes it down before the send finishes, so closing the page does not lose it', async () => {
    const h = open({ send: vi.fn(() => new Promise(() => undefined)) });
    await flush();
    await pick('id1');
    await sendTo(C2);
    expect(h.outbox.items).toHaveLength(1);
    expect(h.outbox.items[0]).toMatchObject({ conversation: C2, body: 'Tối nay ăn gì?' });
  });

  it('keeps it waiting, and says so, when the network is down', async () => {
    const h = open({ send: vi.fn(async () => fail('network', 0)) });
    await flush();
    await pick('id1');
    await sendTo(C2);
    expect(text(root.querySelector('.note')!)).toContain('Sẽ gửi khi có mạng');
    expect(h.outbox.items).toHaveLength(1);
    expect(h.outbox.items[0]).toMatchObject({ conversation: C2, body: 'Tối nay ăn gì?' });
  });

  describe('with pictures and files', () => {
    const att = (id: string, kind: 'image' | 'file' = 'image', name = 'a.webp'): Attachment => ({ id, kind, name, size: 2048, width: 40, height: 30 });
    const withFiles = (body: string): ChatMessage => ({ ...msg(1, 'minh', body), attachments: [att('p1'), att('f1', 'file', 'bảng.xlsx')] });
    const copyOf = () => vi.fn(async (_c: string, from: string) => ok(att(`copy-${from}`, from.startsWith('f') ? 'file' : 'image')));
    const choose = () => sendTo(C2);

    it('copies each picture and file into the other conversation, then sends the words with the copies', async () => {
      const send = echo.mockClear();
      const copyAttachment = copyOf();
      open({ send, copyAttachment }, [withFiles('xem này')]);
      await flush();
      await pick('id1');
      await choose();
      expect(copyAttachment.mock.calls).toEqual([[C2, 'p1'], [C2, 'f1']]);
      expect(send).toHaveBeenCalledWith(C2, expect.any(String), 'xem này', undefined, ['copy-p1', 'copy-f1']);
      expect(text(root)).toContain('Đã chuyển tiếp đến Lan');
    });

    it('passes on a message of only pictures', async () => {
      const send = echo.mockClear();
      open({ send, copyAttachment: copyOf() }, [withFiles('')]);
      await flush();
      await pick('id1');
      await choose();
      expect(send).toHaveBeenCalledWith(C2, expect.any(String), '', undefined, ['copy-p1', 'copy-f1']);
    });

    it('never asks for a copy when the message has no picture or file', async () => {
      const copyAttachment = copyOf();
      open({ send: echo.mockClear(), copyAttachment });
      await flush();
      await pick('id1');
      await choose();
      expect(copyAttachment).not.toHaveBeenCalled();
    });

    it('queues nothing and says a connection is needed when the copy cannot be made for lack of one', async () => {
      const send = echo.mockClear();
      const h = open({ send, copyAttachment: vi.fn(async () => fail('network', 0)) }, [withFiles('xem này')]);
      await flush();
      await pick('id1');
      await choose();
      expect(text(root.querySelector('.note')!)).toContain('Cần có mạng để chuyển ảnh và tệp');
      expect(send).not.toHaveBeenCalled();
      expect(h.outbox.items).toHaveLength(0);
    });

    it('sends nothing when one of the copies is refused, and says why', async () => {
      const send = echo.mockClear();
      const copyAttachment = vi.fn(async (_c: string, from: string) => from === 'p1' ? ok(att('copy-p1')) : fail('not_friends', 403));
      const h = open({ send, copyAttachment }, [withFiles('xem này')]);
      await flush();
      await pick('id1');
      await choose();
      expect(text(root.querySelector('.note')!)).toContain('bạn bè');
      expect(send).not.toHaveBeenCalled();
      expect(h.outbox.items).toHaveLength(0);
    });

    it('goes to the sign-in page when the session has ended', async () => {
      const send = echo.mockClear();
      const h = open({ send, copyAttachment: vi.fn(async () => fail('unauthorized', 401)) }, [withFiles('xem này')]);
      await flush();
      await pick('id1');
      await choose();
      expect(h.go).toHaveBeenCalledWith('/sign-in');
      expect(send).not.toHaveBeenCalled();
    });

    it('writes the message down with the copies before the send finishes', async () => {
      const h = open({ send: vi.fn(() => new Promise(() => undefined)), copyAttachment: copyOf() }, [withFiles('xem này')]);
      await flush();
      await pick('id1');
      await choose();
      expect(h.outbox.items[0]).toMatchObject({ conversation: C2, body: 'xem này' });
      expect(h.outbox.items[0]!.attachments!.map((a) => a.id)).toEqual(['copy-p1', 'copy-f1']);
    });
  });

  describe('to several conversations', () => {
    const C3 = '33333333-3333-4333-8333-333333333333';
    const many = [chat(C1, 'Minh', { lastSeq: 2 }), chat(C2, 'Lan'), chat(C3, 'Hà'),
      ...[4, 5, 6, 7].map((n) => chat(`${n}${n}${n}${n}${n}${n}${n}${n}-${n}${n}${n}${n}-4${n}${n}${n}-8${n}${n}${n}-${String(n).repeat(12)}`, `Nhóm ${n}`))];
    const openMany = (extra: Record<string, unknown> = {}) =>
      start({ path: `/c/${C1}`, chats: many, history: { [C1]: [msg(1, 'minh', 'Tối nay ăn gì?')] }, api: extra });

    it('sends to each ticked conversation, then the note after it, and names them all', async () => {
      const send = echo.mockClear();
      openMany({ send });
      await flush();
      await pick('id1');
      root.querySelector<HTMLTextAreaElement>('[data-forward-note]')!.value = 'xem nhé';
      root.querySelector<HTMLTextAreaElement>('[data-forward-note]')!.dispatchEvent(new Event('input', { bubbles: true }));
      await sendTo(C2, C3);
      expect(send.mock.calls.map((c) => [c[0], c[2]])).toEqual([[C2, 'Tối nay ăn gì?'], [C2, 'xem nhé'], [C3, 'Tối nay ăn gì?'], [C3, 'xem nhé']]);
      expect(text(root.querySelector('.note')!)).toBe('Đã chuyển tiếp đến Lan, Hà.');
      expect(root.querySelector('[data-forward-panel]')).toBeNull();
    });

    it('says the button is for the ticked count', async () => {
      openMany();
      await flush();
      await pick('id1');
      tick(C2); tick(C3);
      await flush();
      expect(text(root.querySelector('[data-forward-send]')!)).toBe('Chuyển tiếp (2)');
    });

    it('narrows the list by name, and keeps what was ticked while it is hidden', async () => {
      openMany();
      await flush();
      await pick('id1');
      tick(C3);
      await flush();
      fill(root, '[data-forward-search]', 'lan');
      await flush();
      expect([...root.querySelectorAll('[data-forward-pick]')].map((b) => b.getAttribute('data-forward-pick'))).toEqual([C2]);
      fill(root, '[data-forward-search]', 'zzz');
      await flush();
      expect(root.querySelector('[data-forward-none]')).not.toBeNull();
      fill(root, '[data-forward-search]', '');
      await flush();
      expect(root.querySelector<HTMLInputElement>(`[data-forward-pick="${C3}"]`)!.checked).toBe(true);
    });

    it('lets at most five be ticked: the others wait, and it says why', async () => {
      openMany();
      await flush();
      await pick('id1');
      const ids = [...root.querySelectorAll('[data-forward-pick]')].map((b) => b.getAttribute('data-forward-pick')!);
      for (const id of ids.slice(0, 5)) tick(id);
      await flush();
      expect(root.querySelector('[data-forward-full]')).not.toBeNull();
      expect(root.querySelector<HTMLInputElement>(`[data-forward-pick="${ids[5]}"]`)!.disabled).toBe(true);
      expect(root.querySelector<HTMLInputElement>(`[data-forward-pick="${ids[0]}"]`)!.disabled).toBe(false);   // a ticked one can be unticked
      tick(ids[0]!);
      await flush();
      expect(root.querySelector('[data-forward-full]')).toBeNull();
      expect(root.querySelector<HTMLInputElement>(`[data-forward-pick="${ids[5]}"]`)!.disabled).toBe(false);
    });

    it('says which ones were refused and why, and which went', async () => {
      const send = vi.fn(async (conversation: string, clientId: string, body: string) =>
        conversation === C3 ? fail('not_friends', 403) : ok({ ...msg(1, 'mom', body, at(11), conversation), id: `fw-${clientId}`, clientId }));
      openMany({ send });
      await flush();
      await pick('id1');
      await sendTo(C2, C3);
      const said = text(root.querySelector('.note')!);
      expect(said).toContain('Đã chuyển tiếp đến Lan.');
      expect(said).toContain('Hà: ');
      expect(said).toContain('bạn bè');
    });

    it('sends no note where the message itself was refused', async () => {
      const send = vi.fn(async () => fail('not_friends', 403));
      openMany({ send });
      await flush();
      await pick('id1');
      fill(root, '[data-forward-note]', 'xem nhé');
      await sendTo(C2);
      expect(send).toHaveBeenCalledTimes(1);
    });

    it('closes when the browser closes the dialog, sending nothing', async () => {
      const send = echo.mockClear();
      openMany({ send });
      await flush();
      await pick('id1');
      tick(C2);
      root.querySelector('[data-forward-panel]')!.dispatchEvent(new Event('close'));
      await flush();
      expect(root.querySelector('[data-forward-panel]')).toBeNull();
      expect(send).not.toHaveBeenCalled();
    });
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

  const choose = async (key: string) => {
    bell().click();
    await flush();
    root.querySelector<HTMLButtonElement>(`[data-mute-panel] [data-mute-for="${key}"]`)!.click();
  };
  const until = (h: { api: { setMute: ReturnType<typeof vi.fn> } }) => new Date(h.api.setMute.mock.calls[0]![2] as string);

  it('asks for how long before muting, and mutes nothing until a choice is made', async () => {
    const h = open();
    await flush();
    bell().click();
    await flush();
    const panel = root.querySelector('[data-mute-panel]')!;
    expect([...panel.querySelectorAll('[data-mute-for]')].map((b) => b.textContent!.trim()))
      .toEqual(['Trong 1 giờ', 'Trong 4 giờ', 'Cho đến 8:00 sáng', 'Cho đến khi được mở lại']);
    expect(bell().getAttribute('aria-pressed')).toBe('false');
    expect(h.api.setMute).not.toHaveBeenCalled();
    panel.dispatchEvent(new Event('close'));
    await flush();
    expect(root.querySelector('[data-mute-panel]')).toBeNull();
    expect(h.api.setMute).not.toHaveBeenCalled();
  });

  it('mutes until turned back on at once, and asks the server with no end', async () => {
    const h = open();
    await flush();
    await choose('always');
    expect(bell().getAttribute('aria-pressed')).toBe('true');
    expect(root.querySelector('[data-mute-panel]')).toBeNull();
    await flush();
    expect(h.api.setMute).toHaveBeenCalledWith(C2, true, undefined);
    expect(root.querySelector(`[data-chat="${C2}"] .mute-mark`)).not.toBeNull();
  });

  it('mutes for one hour or four from now', async () => {
    for (const [key, hours] of [['1h', 1], ['4h', 4]] as const) {
      const h = open();
      await flush();
      await choose(key);
      await flush();
      expect(until(h).getTime() - NOW.getTime()).toBe(hours * 3_600_000);
      expect(bell().getAttribute('aria-pressed')).toBe('true');
    }
  });

  it('mutes until the next 8 in the morning by the phone\'s own clock', async () => {
    const h = open();
    await flush();
    await choose('8am');
    await flush();
    const end = until(h);
    expect([end.getHours(), end.getMinutes(), end.getSeconds()]).toEqual([8, 0, 0]);
    expect(end.getTime()).toBeGreaterThan(NOW.getTime());
    expect(end.getTime() - NOW.getTime()).toBeLessThanOrEqual(24 * 3_600_000);
  });

  it('says until when a mute for a while lasts, in the side panel', async () => {
    open();
    await flush();
    await choose('1h');
    await flush();
    root.querySelector<HTMLButtonElement>('button[data-info]')!.click();
    await flush();
    expect(root.querySelector('[data-muted-until]')!.textContent).toContain(clock(new Date(NOW.getTime() + 3_600_000).toISOString()));
  });

  it('treats a mute for a while that has ended as not muted', async () => {
    start({ path: '/', chats: [chat(C1, 'Minh', { lastSeq: 3, unread: 2, muted: true, mutedUntil: new Date(NOW.getTime() - 1000).toISOString() })], history: {}, api: {} });
    await flush();
    const row = root.querySelector(`[data-chat="${C1}"]`)!;
    expect(row.querySelector('.mute-mark')).toBeNull();
    expect(row.querySelector('.badge')!.classList.contains('muted')).toBe(false);
  });

  it('unmutes with one tap, without asking how long', async () => {
    const h = open({}, `/c/${C1}`);
    await flush();
    bell().click();
    await flush();
    expect(root.querySelector('[data-mute-panel]')).toBeNull();
    expect(h.api.setMute).toHaveBeenCalledWith(C1, false, undefined);
    expect(bell().getAttribute('aria-pressed')).toBe('false');
  });

  it('puts the switch back and says why when the server refuses', async () => {
    open({ setMute: vi.fn(async () => fail('server', 500)) });
    await flush();
    await choose('4h');
    await flush();
    expect(bell().getAttribute('aria-pressed')).toBe('false');
    expect(root.querySelector('[role="alert"]')).not.toBeNull();
  });
});

describe('chat: notifications on this device', () => {
  const chats = [chat(C1, 'Minh', { lastSeq: 1 }), chat(C2, 'Lan', { lastSeq: 1 })];

  it('closes a conversation\'s notifications when it is opened, and again when the page comes back on screen', async () => {
    const h = start({ path: '/', chats, history: { [C1]: [msg(1, 'minh')], [C2]: [msg(1, 'minh', 'x', at(10, 1), C2)] }, api: {} });
    await flush();
    expect(h.notes).not.toHaveBeenCalled();
    root.querySelector<HTMLAnchorElement>(`[data-chat="${C2}"] a, a[data-chat="${C2}"]`)!.click();
    await flush();
    expect(h.notes).toHaveBeenCalledWith(C2);
    h.notes.mockClear();
    h.visible.value = false;
    document.dispatchEvent(new Event('visibilitychange'));
    expect(h.notes).not.toHaveBeenCalled();
    h.visible.value = true;
    document.dispatchEvent(new Event('visibilitychange'));
    expect(h.notes).toHaveBeenCalledWith(C2);
  });
});

describe('chat: the offer to turn on notifications', () => {
  const SUB = { endpoint: 'https://fcm.googleapis.com/fcm/send/x', keys: { p256dh: 'k', auth: 'a' } };
  const device = (over: { support?: PushSupport; permission?: NotificationPermission; subscribe?: Pusher['subscribe'] } = {}) => ({
    support: vi.fn(() => over.support ?? 'ok'), permission: vi.fn(() => over.permission ?? 'default'),
    subscribe: vi.fn(over.subscribe ?? (async () => SUB)), current: vi.fn(async () => SUB), unsubscribe: vi.fn(async () => undefined), clear: vi.fn(),
  });
  const offered = (subscribed = false) => vi.fn(async () => ok({ key: 'SERVERKEY', subscribed, level: 'name' as const }));
  const open = (pusher: Pusher | undefined, api: Record<string, unknown> = { pushState: offered() }) => start({ path: '/', chats: [chat(C1, 'Minh')], api, ...(pusher ? { pusher } : {}) });
  const bar = () => root.querySelector('[data-push-banner]');
  beforeEach(() => { try { localStorage.removeItem('gom-push-later'); } catch { /* none */ } });

  it('offers it on a device that can have it, and turns it on from the tap', async () => {
    const p = device();
    const h = open(p);
    await flush();
    expect(text(bar()!)).toContain('Bật thông báo');
    root.querySelector<HTMLButtonElement>('[data-push-yes]')!.click();
    await flush();
    expect(p.subscribe).toHaveBeenCalledWith('SERVERKEY');
    expect(h.api.setPush).toHaveBeenCalledWith(SUB, 'name');
    expect(bar()).toBeNull();
  });

  it('"Để sau" hides it on this device for good', async () => {
    open(device());
    await flush();
    root.querySelector<HTMLButtonElement>('[data-push-later]')!.click();
    await flush();
    expect(bar()).toBeNull();
    root = document.createElement('div'); document.body.replaceChildren(root);   // a fresh page
    open(device());
    await flush();
    expect(bar()).toBeNull();
  });

  it('is not offered where it cannot work or is already on', async () => {
    for (const [p, api] of [
      [undefined, { pushState: offered() }],
      [device({ support: 'unsupported' }), { pushState: offered() }],
      [device({ support: 'needs-home-screen' }), { pushState: offered() }],
      [device({ permission: 'denied' }), { pushState: offered() }],
      [device(), { pushState: offered(true) }],
      [device(), { pushState: vi.fn(async () => fail('unknown', 404)) }],
    ] as const) {
      root = document.createElement('div'); document.body.replaceChildren(root);   // a fresh page
      open(p as Pusher | undefined, api);
      await flush();
      expect(bar()).toBeNull();
    }
  });

  it('goes away when the person says no to the browser, and says so when the browser fails', async () => {
    const no = device({ subscribe: async () => 'denied' });
    const h = open(no);
    await flush();
    root.querySelector<HTMLButtonElement>('[data-push-yes]')!.click();
    await flush();
    expect(bar()).toBeNull();
    expect(h.api.setPush).not.toHaveBeenCalled();

    root = document.createElement('div'); document.body.replaceChildren(root);   // a fresh page
    open(device({ subscribe: async () => 'failed' }));
    await flush();
    root.querySelector<HTMLButtonElement>('[data-push-yes]')!.click();
    await flush();
    expect(text(bar()!)).toContain('Không bật được');
    expect(root.querySelector('[data-push-yes]')).toBeNull();
  });

  it('says once that a browser without a push service (Helium) cannot get them, and does not offer them there again', async () => {
    open(device({ subscribe: async () => 'no-service' }));
    await flush();
    root.querySelector<HTMLButtonElement>('[data-push-yes]')!.click();
    await flush();
    expect(text(bar()!)).toContain('không có dịch vụ thông báo đẩy');
    expect(text(bar()!)).toContain('Helium');
    expect(root.querySelector('[data-push-yes]')).toBeNull();

    root = document.createElement('div'); document.body.replaceChildren(root);   // a fresh page on the same device
    open(device({ subscribe: async () => 'no-service' }));
    await flush();
    expect(bar()).toBeNull();
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

  it('takes its pictures out of the side panel too', async () => {
    localStorage.setItem('gom-info', '1');
    const picture = { attachment: { id: 'px', kind: 'image' as const, name: 'a.webp', size: 1, width: 4, height: 3 }, messageId: 'id1', seq: 1, createdAt: at(10) };
    open({ media: vi.fn(async (_c: string, kind: string) => ok({ items: kind === 'image' ? [picture] : [], hasMore: false })) });
    await flush();
    expect(root.querySelector('[data-info-pic="px"]')).not.toBeNull();
    await menuOf('id1');
    root.querySelector<HTMLButtonElement>('button[data-hide]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('[data-confirm-hide]')!.click();
    await flush();
    expect(root.querySelector('[data-info-pic="px"]')).toBeNull();
    localStorage.removeItem('gom-info');
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
  let layout: ReturnType<typeof fakeLayout>;
  beforeEach(() => { layout = fakeLayout(); });
  afterEach(() => layout.restore());
  const history = { [C1]: [msg(1, 'mom'), msg(2, 'minh'), msg(3, 'minh')] };
  const unreadChats = [withLast(msg(3, 'minh'), { lastReadSeq: 1, unread: 2 }), chat(C2, 'Lan')];

  it('scrolls to the first unread message when a conversation with unread messages is opened', async () => {
    start({ path: `/c/${C1}`, chats: unreadChats, history });
    await flush();
    expect(layout.landed()?.classList.contains('newdiv')).toBe(true);
    expect(layout.intoView()).toBe(0);   // never scrollIntoView: it moves the page too, and the header goes off screen
  });

  it('does not scroll to a divider when nothing is unread', async () => {
    start({ path: `/c/${C1}`, chats: [withLast(msg(3, 'minh'), { lastReadSeq: 3, unread: 0 })], history });
    await flush();
    expect(layout.landed()).toBeNull();
  });

  it('jumps once, and a new message afterwards does not drag you back to the divider', async () => {
    const h = start({ path: `/c/${C1}`, chats: unreadChats, history });
    await flush();
    expect(layout.landed()?.classList.contains('newdiv')).toBe(true);
    const box = root.querySelector<HTMLElement>('.msgs')!;
    box.scrollTop = 7;   // the person has moved on from there
    h.handlers().event(C1, created(msg(4, 'minh', 'mới')));
    await flush();
    expect(box.scrollTop).toBe(7);
  });

  it('also jumps when you open a conversation you had already loaded', async () => {
    const h = start({ chats: unreadChats, history });
    await flush();
    root.querySelectorAll<HTMLAnchorElement>('a[data-chat]')[0]!.click();
    await flush();
    expect(layout.landed()?.classList.contains('newdiv')).toBe(true);
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

  describe('the arrow in the corner to the end of the conversation', () => {
    const read = [withLast(msg(3, 'minh'), { lastReadSeq: 3, unread: 0 })];
    const arrow = () => root.querySelector<HTMLButtonElement>('[data-to-end]');
    /** jsdom has no layout: say how long the thread is, how much shows, and where it is scrolled to, and let the page see it scroll. */
    const scrollTo = async (top: number) => {
      const box = root.querySelector<HTMLElement>('.msgs')!;
      Object.defineProperty(box, 'scrollHeight', { value: 1000, configurable: true });
      Object.defineProperty(box, 'clientHeight', { value: 300, configurable: true });
      Object.defineProperty(box, 'scrollTop', { value: top, configurable: true, writable: true });
      box.dispatchEvent(new Event('scroll'));
      await flush();
      return box;
    };

    it('is not there while you are at the end', async () => {
      start({ path: `/c/${C1}`, chats: read, history });
      await flush();
      expect(arrow()).toBeNull();
      await scrollTo(650);   // 50 px from the end: still "at the end"
      expect(arrow()).toBeNull();
    });

    it('appears when you read further up, with nothing new, and goes when you are back near the end', async () => {
      start({ path: `/c/${C1}`, chats: read, history });
      await flush();
      await scrollTo(100);
      expect(arrow()).not.toBeNull();
      expect(arrow()!.getAttribute('aria-label')).toBe('Xuống tin mới nhất');
      expect(root.querySelector('[data-jump-bottom]')).toBeNull();   // no count: nothing arrived
      await scrollTo(650);
      expect(arrow()).toBeNull();
    });

    it('takes you to the end when pressed, and goes', async () => {
      start({ path: `/c/${C1}`, chats: read, history });
      await flush();
      const box = await scrollTo(100);
      arrow()!.click();
      await flush();
      expect(box.scrollTop).toBe(1000);
      expect(arrow()).toBeNull();
    });

    it('stays beside the count of new messages, and pressing it clears the count too', async () => {
      const h = start({ path: `/c/${C1}`, chats: read, history });
      await flush();
      await scrollTo(100);
      h.handlers().event(C1, created(msg(4, 'minh', 'mới')));
      await flush();
      expect(arrow()).not.toBeNull();
      expect(root.querySelector('[data-jump-bottom]')).not.toBeNull();
      arrow()!.click();
      await flush();
      expect(root.querySelector('[data-jump-bottom]')).toBeNull();
      expect(arrow()).toBeNull();
    });

  });
});

describe('chat: searching a conversation', () => {
  let layout: ReturnType<typeof fakeLayout>;
  beforeEach(() => { layout = fakeLayout(); });
  afterEach(() => layout.restore());
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

  it('searches as you type once typing pauses, from two characters; Enter searches even one', async () => {
    const h = open({ search: vi.fn(async () => ok({ results: [hit(messages[0]!)] })) });
    await flush();
    await toggle();
    const input = root.querySelector<HTMLInputElement>('.searchbar input')!;
    const typing = (q: string) => { input.value = q; input.dispatchEvent(new Event('input', { bubbles: true })); };
    const pauses = h.timers.filter((t) => t.ms === 300);
    typing('p');
    expect(h.timers.filter((t) => t.ms === 300)).toEqual(pauses);   // one character: nothing until Enter
    typing('ph');
    typing('phở');
    const waits = h.timers.filter((t) => t.ms === 300 && t.live);
    expect(waits).toHaveLength(1);   // each key starts the wait again
    expect(h.api.search).not.toHaveBeenCalled();
    waits[0]!.fn();
    await flush();
    expect(h.api.search).toHaveBeenCalledWith(C1, 'phở');
    expect(root.querySelector('[data-result="id1"]')).not.toBeNull();
    await ask('ă');
    expect(h.api.search).toHaveBeenLastCalledWith(C1, 'ă');
  });

  it('takes the results away when the field is emptied, and an answer still on its way is dropped', async () => {
    const finish: Array<(v: unknown) => void> = [];
    const search = vi.fn(() => new Promise((resolve) => { finish.push(resolve); }));
    open({ search });
    await flush();
    await toggle();
    await ask('phở');
    const input = root.querySelector<HTMLInputElement>('.searchbar input')!;
    input.value = ''; input.dispatchEvent(new Event('input', { bubbles: true }));
    finish[0]!(ok({ results: [hit(messages[0]!)] })); await flush();
    expect(root.querySelector('[data-result="id1"]')).toBeNull();
    expect(root.querySelector('[data-no-results]')).toBeNull();
  });

  it('shows the words found in bold, whatever their accents', async () => {
    open({ search: vi.fn(async () => ok({ results: [hit(messages[0]!)] })) });
    await flush();
    await toggle();
    await ask('an pho');
    expect([...root.querySelectorAll('[data-result="id1"] mark')].map((m) => m.textContent)).toEqual(['ăn', 'phở']);
  });

  it('takes you to a result that is already on screen, and marks it for a moment', async () => {
    const h = open({ search: vi.fn(async () => ok({ results: [hit(messages[0]!)] })) });
    await flush();
    await toggle();
    await ask('phở');
    root.querySelector<HTMLButtonElement>('[data-result="id1"]')!.click();
    await flush();
    expect(root.querySelector('.searchbar')).toBeNull();
    expect(layout.landed()?.getAttribute('data-msg')).toBe('id1');
    expect(layout.intoView()).toBe(0);
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
  let layout: ReturnType<typeof fakeLayout>;
  beforeEach(() => { layout = fakeLayout(); });
  afterEach(() => layout.restore());
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
    root.querySelector<HTMLElement>('.msgs')!.scrollTop = 500;   // read further down: the jump is from here
    root.querySelector<HTMLButtonElement>('[data-pin-top]')!.click();
    await flush();
    expect(layout.landed()?.getAttribute('data-msg')).toBe('id1');
    expect(layout.intoView()).toBe(0);
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

  describe('finding friends and groups by name', () => {
    const C3 = '33333333-3333-4333-8333-333333333333';
    const mixed = [chat(C1, 'Đào Thị Lan', { lastSeq: 0 }), { ...chat(C2, 'Gia đình', { lastSeq: 0 }), kind: 'group' as const, peer: null }];
    const friendList = (friends: Array<{ gomId: string; displayName: string }>) => vi.fn(async () => ok({ friends, incoming: [], outgoing: [] }));
    const heading = (attr: string) => root.querySelector(`[data-${attr}]`);
    const under = (attr: string) => [...(heading(attr)?.nextElementSibling?.querySelectorAll('.nm') ?? [])].map((n) => n.textContent);

    it('lists friends first, then groups, each under its heading, and never searches messages', async () => {
      const h = start({ chats: mixed, api: { friends: friendList([{ gomId: 'đào thị lan', displayName: 'Đào Thị Lan' }, { gomId: 'minh', displayName: 'Minh' }]) } });
      await flush();
      expect(heading('found-friends')).toBeNull();   // nothing typed: the plain list
      type('i'); await flush();
      expect(text(heading('found-friends')!)).toBe('Bạn bè');
      expect(under('found-friends')).toEqual(['Đào Thị Lan', 'Minh']);   // the conversation first, then a friend not written to
      expect(text(heading('found-groups')!)).toBe('Nhóm');
      expect(under('found-groups')).toEqual(['Gia đình']);
      expect(h.api.friends).toHaveBeenCalledTimes(1);
      type('in'); await flush();
      expect(h.api.friends).toHaveBeenCalledTimes(1);   // asked once for one search
      for (const t of h.timers) if (t.live) t.fn();
      await flush();
      expect(h.api.search).not.toHaveBeenCalled();
    });

    it('finds a friend by Gom ID too, and leaves out a heading with nothing under it', async () => {
      start({ chats: mixed, api: { friends: friendList([{ gomId: 'beo', displayName: 'Hoa' }]) } });
      await flush();
      type('beo'); await flush();
      expect(under('found-friends')).toEqual(['Hoa']);
      expect(text(root.querySelector('[data-start-chat="beo"]')!)).toContain('@beo');
      expect(heading('found-groups')).toBeNull();
    });

    it('opens a conversation with a friend not written to yet, and the search is done', async () => {
      const h = start({ chats: mixed, api: { friends: friendList([{ gomId: 'minh', displayName: 'Minh' }]), openDirect: vi.fn(async () => ok({ id: C3 })) } });
      await flush();
      type('minh'); await flush();
      root.querySelector<HTMLButtonElement>('[data-start-chat="minh"]')!.click();
      await flush(); await flush();
      expect(h.api.openDirect).toHaveBeenCalledWith('minh');
      expect(h.nav.path).toBe(`/c/${C3}`);
      expect(root.querySelector<HTMLInputElement>('input[name="filter"]')!.value).toBe('');
      expect(heading('found-friends')).toBeNull();
    });

    it('still finds a hidden conversation, under its own heading', async () => {
      start({ chats: [...mixed, chat(C3, 'Cường', { hidden: true })], api: { friends: friendList([]) } });
      await flush();
      expect(names()).not.toContain('Cường');
      type('cuong'); await flush();
      expect(text(root.querySelector('[data-hidden-label]')!)).toBe('Đã ẩn');
      expect([...root.querySelectorAll('[data-hidden-list] .nm')].map((n) => n.textContent)).toEqual(['Cường']);
      expect(heading('found-friends')).toBeNull();   // a hidden one is not listed twice
    });
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

  it('sets a conversation\'s own colour and background from its info panel, for everyone, and draws its pane in them', async () => {
    const setLook = vi.fn(async () => ok(undefined));
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh'), chat(C2, 'Lan', { palette: 'lotus' })], history: { [C1]: [msg(1, 'minh')] }, api: { setLook } });
    await flush();
    expect(root.querySelector('[data-chat="' + C2 + '"] .look-dot')!.getAttribute('data-palette')).toBe('lotus');
    expect(root.querySelector('[data-chat="' + C1 + '"] .look-dot')).toBeNull();
    expect(root.querySelector('.thread-pane')!.hasAttribute('data-palette')).toBe(false);

    root.querySelector<HTMLButtonElement>('[data-info]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('[data-info-look]')!.click();
    await flush();
    const panel = () => root.querySelector<HTMLElement>('[data-look-panel]')!;
    expect(panel().querySelectorAll('[data-look-colours] input')).toHaveLength(25);   // each person's own, then sixteen colours and eight gradients
    expect(panel().querySelector<HTMLInputElement>('[data-look-colours] input[value=""]')!.checked).toBe(true);
    const sea = panel().querySelector<HTMLInputElement>('[data-look-colours] input[value="sea"]')!;
    sea.checked = true; sea.dispatchEvent(new Event('change'));
    panel().querySelector<HTMLButtonElement>('[data-look-tab="pattern"]')!.click();
    await flush();
    expect(panel().querySelectorAll('[data-look-patterns] input')).toHaveLength(16);
    const waves = panel().querySelector<HTMLInputElement>('[data-look-patterns] input[value="waves"]')!;
    waves.checked = true; waves.dispatchEvent(new Event('change'));
    panel().querySelector<HTMLButtonElement>('[data-look-save]')!.click();
    await flush();
    expect(setLook).toHaveBeenCalledWith(C1, 'sea', 'waves');
    expect(root.querySelector('[data-look-panel]')).toBeNull();
    expect(root.querySelector('.thread-pane')!.getAttribute('data-palette')).toBe('sea');
    expect(root.querySelector<HTMLElement>('.thread-pane .backdrop')!.dataset['pattern']).toBe('waves');
  });

  it('keeps the dialog open with the reason when the server says no', async () => {
    const setLook = vi.fn(async () => fail('not_friends', 403));
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh', { palette: 'jade', pattern: 'dots' })], history: { [C1]: [msg(1, 'minh')] }, api: { setLook } });
    await flush();
    expect(root.querySelector('.thread-pane')!.getAttribute('data-palette')).toBe('jade');
    root.querySelector<HTMLButtonElement>('[data-info]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('[data-info-look]')!.click();
    await flush();
    const each = root.querySelector<HTMLInputElement>('[data-look-panel] [data-look-colours] input[value=""]')!;
    expect(root.querySelector<HTMLInputElement>('[data-look-panel] input[value="jade"]')!.checked).toBe(true);
    each.checked = true; each.dispatchEvent(new Event('change'));
    root.querySelector<HTMLButtonElement>('[data-look-save]')!.click();
    await flush();
    expect(setLook).toHaveBeenCalledWith(C1, null, 'dots');
    expect(root.querySelector('[data-look-panel]')).not.toBeNull();
    expect(root.querySelector('.thread-pane')!.getAttribute('data-palette')).toBe('jade');
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

describe('chat: videos', () => {
  const clip = (name = 'clip.mov', bytes = 3) => new File([new Uint8Array(bytes)], name, { type: 'video/quicktime' });
  const video = (id: string, extra: Partial<Attachment> = {}): Attachment => ({ id, kind: 'video', name: 'clip.mp4', size: 4096, width: 1280, height: 720, durationMs: 42_000, ...extra });
  const open = (opts: { api?: Record<string, unknown>; history?: ChatMessage[]; prepareVideo?: ChatDeps['prepareVideo'] } = {}) =>
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh', { lastSeq: opts.history?.length ?? 0 })], history: { [C1]: opts.history ?? [] }, api: opts.api ?? {}, ...(opts.prepareVideo ? { prepareVideo: opts.prepareVideo } : {}) });
  const pickGallery = async (files: File[]) => {
    const input = root.querySelector<HTMLInputElement>('input[name="gallery"]')!;
    Object.defineProperty(input, 'files', { value: files, configurable: true });
    input.dispatchEvent(new Event('change', { bubbles: true }));
    await flush(); await flush();
  };

  it('has a button for pictures and videos beside the one for any file, as on a phone', async () => {
    open();
    await flush();
    const gallery = root.querySelector<HTMLInputElement>('input[name="gallery"]')!;
    expect(gallery.accept).toBe('image/*,video/*');
    expect(gallery.multiple).toBe(true);
    expect(root.querySelector('[data-gallery]')!.getAttribute('aria-label')).toBe('Gửi ảnh hoặc video');
    expect(root.querySelector<HTMLInputElement>('input[name="files"]')!.accept).toBe('');
  });

  it('makes a video smaller on the device, showing how far it is, then sends it as an MP4 with its still picture', async () => {
    let progress: (f: number) => void = () => undefined;
    let done: (v: { video: Blob; poster: Blob | null }) => void = () => undefined;
    const prepareVideo = vi.fn((_f: File, onProgress: (f: number) => void, _max: number) => { progress = onProgress; return new Promise<{ video: Blob; poster: Blob | null }>((r) => { done = r; }); });
    const upload = vi.fn(async (_c: string, file: File) => ok(video('v1', { name: file.name })));
    const setPoster = vi.fn(async () => ok(undefined));
    const h = open({ api: { upload, setPoster }, prepareVideo });
    await flush();
    await pickGallery([clip('Nhà mình.mov')]);
    expect(prepareVideo).toHaveBeenCalledOnce();
    expect(prepareVideo.mock.calls[0]![2]).toBe(45 * 1024 * 1024);
    const bar = root.querySelector<HTMLProgressElement>('[data-tray-converting]')!;
    expect(bar.getAttribute('aria-label')).toBe('Đang chuẩn bị video Nhà mình.mov');
    progress(0.5); await flush();
    expect(root.querySelector<HTMLProgressElement>('[data-tray-converting]')!.value).toBe(0.5);
    expect(root.querySelector<HTMLButtonElement>('.composer .send')!.disabled).toBe(true);
    expect(upload).not.toHaveBeenCalled();

    const poster = new Blob([new Uint8Array([9])], { type: 'image/jpeg' });
    done({ video: new Blob([new Uint8Array(5)], { type: 'video/mp4' }), poster });
    await flush(); await flush(); await flush();
    const sent = upload.mock.calls[0]![1];
    expect(sent.name).toBe('Nhà mình.mp4');
    expect(sent.type).toBe('video/mp4');
    expect(sent.size).toBe(5);
    expect(setPoster).toHaveBeenCalledWith('v1', poster);
    expect(root.querySelector('[data-tray-item] img')!.getAttribute('src')).toMatch(/^data:image\/jpeg;base64,/);
    expect(root.querySelector<HTMLButtonElement>('.composer .send')!.disabled).toBe(false);
    expect(h.api.upload).toHaveBeenCalledOnce();
  });

  it('sends the video as it is when the device cannot make it smaller, and refuses one over the limit before any upload', async () => {
    const upload = vi.fn(async (_c: string, _f: File) => ok(video('v1')));
    open({ api: { upload }, prepareVideo: vi.fn(async () => null) });
    await flush();
    const small = clip('a.mp4', 10);
    await pickGallery([small]);
    expect(upload.mock.calls[0]![1]).toBe(small);

    const big = clip('b.mp4');
    Object.defineProperty(big, 'size', { value: 45 * 1024 * 1024 + 1 });
    await pickGallery([big]);
    expect(upload).toHaveBeenCalledOnce();
    expect(text(root.querySelector('[data-tray-item].failed [role="alert"]')!)).toContain('Video quá lớn (tối đa 45 MB, khoảng 8 phút).');
  });

  it('plays a video in its message with its still picture and length, and offers a download where it cannot play', async () => {
    open({ history: [{ ...msg(1, 'minh', ''), attachments: [video('v1')] }] });
    await flush();
    const player = root.querySelector<HTMLVideoElement>('[data-video="v1"] video')!;
    expect(player.getAttribute('src')).toBe('/api/attachments/v1');
    expect(player.getAttribute('poster')).toBe('/api/attachments/v1/thumb');
    expect(player.controls).toBe(true);
    expect(player.getAttribute('preload')).toBe('metadata');
    expect(player.getAttribute('aria-label')).toBe('Video clip.mp4, dài 0:42');
    expect(text(root.querySelector('[data-video="v1"] .vid-len')!)).toBe('0:42');
    player.dispatchEvent(new Event('error'));
    await flush();
    expect(root.querySelector('[data-video="v1"]')).toBeNull();
    expect(root.querySelector('a[data-video-download="v1"]')!.getAttribute('download')).toBe('clip.mp4');
  });

  it('shows a video in the grid of pictures with a play mark, and plays it in the viewer', async () => {
    const v = video('v1');
    const shown = { ...msg(1, 'minh', ''), attachments: [v] };
    const listed = { attachment: v, messageId: shown.id, seq: 1, createdAt: shown.createdAt, sender: shown.sender, body: '' };
    open({ history: [shown], api: { media: vi.fn(async (_c: string, kind: string) => ok({ items: kind === 'image' ? [listed] : [], hasMore: false })) } });
    await flush();
    root.querySelector<HTMLButtonElement>('[data-info-page-open]')!.click();
    await flush(); await flush();
    const tile = root.querySelector<HTMLButtonElement>('[data-info-pic="v1"]')!;
    expect(tile.classList.contains('vtile')).toBe(true);
    expect(tile.getAttribute('aria-label')).toBe('Video clip.mp4, dài 0:42');
    tile.click();
    await flush();
    const player = root.querySelector<HTMLVideoElement>('[data-viewer] [data-viewer-slide="v1"] video')!;
    expect(player.getAttribute('src')).toBe('/api/attachments/v1');
    expect(player.controls).toBe(true);
  });

  it('says a video was sent in the list', async () => {
    start({ chats: [chat(C1, 'Minh', { lastSeq: 1, lastMessage: { seq: 1, body: '', createdAt: at(10, 1), sender: { id: 'u-minh', displayName: 'Minh' }, attachment: 'video' } })] });
    await flush();
    expect(text(root.querySelector('[data-chat] .pv')!)).toBe('Đã gửi một video');
  });

  it('says a trimmed video is gone, keeping its place', async () => {
    open({ history: [{ ...msg(1, 'minh', ''), attachments: [video('v2', { removed: true })] }] });
    await flush();
    expect(text(root.querySelector('[data-video-removed="v2"]')!)).toBe('Video không còn khả dụng');
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

  it('takes a GIF from the app\'s keyboard into the open conversation, only while its message box has the focus', async () => {
    const upload = vi.fn(async () => ok(att('g1')));
    let hand: (file: File) => void = () => { throw new Error('not handed over'); };
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh')], history: { [C1]: [] }, api: { upload }, keyboardImages: (callback) => { hand = callback; } });
    await flush();
    const gif = new File([new Uint8Array([71, 73, 70])], 'keyboard.gif', { type: 'image/gif' });
    hand(gif);   // the box does not have the focus: it went elsewhere (search, a dialog)
    await flush();
    expect(upload).not.toHaveBeenCalled();
    root.querySelector<HTMLTextAreaElement>('textarea[name="message"]')!.focus();
    hand(gif);
    await flush();
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

  it('plays a moving picture in the message, and its own button pauses it on the still first frame and plays it again', async () => {
    const m: ChatMessage = { ...msg(1, 'minh', '', at(10)), attachments: [{ ...att('g1', 'image', 'meo.gif'), animated: true }, att('p2')] };
    open({}, [m]);
    await flush();
    const moving = root.querySelector<HTMLElement>('[data-moving="g1"]')!;
    const img = () => moving.querySelector('img')!.getAttribute('src');
    const toggle = () => moving.querySelector<HTMLButtonElement>('[data-moving-toggle]')!;
    expect(img()).toBe('/api/attachments/g1');
    expect(toggle().getAttribute('aria-label')).toBe('Dừng ảnh động');
    toggle().click();
    await flush();
    expect(img()).toBe('/api/attachments/g1/thumb');
    expect(toggle().getAttribute('aria-label')).toBe('Phát ảnh động');
    toggle().click();
    await flush();
    expect(img()).toBe('/api/attachments/g1');
    // A still picture beside it is drawn as before, and the moving one still opens the viewer.
    expect(root.querySelector('[data-pic="p2"] img')!.getAttribute('src')).toBe('/api/attachments/p2/thumb');
    moving.querySelector<HTMLButtonElement>('[data-pic="g1"]')!.click();
    await flush();
    expect(root.querySelector('[data-viewer]')).not.toBeNull();
  });

  it('starts a moving picture paused when the device asks for less motion', async () => {
    const match = vi.fn((q: string) => ({ matches: q.includes('reduce'), media: q, addEventListener() {}, removeEventListener() {} }));
    vi.stubGlobal('matchMedia', match);
    try {
      const m: ChatMessage = { ...msg(1, 'minh', '', at(10)), attachments: [{ ...att('g1', 'image', 'meo.gif'), animated: true }] };
      open({}, [m]);
      await flush();
      expect(root.querySelector('[data-moving="g1"] img')!.getAttribute('src')).toBe('/api/attachments/g1/thumb');
      root.querySelector<HTMLButtonElement>('[data-moving-toggle]')!.click();
      await flush();
      expect(root.querySelector('[data-moving="g1"] img')!.getAttribute('src')).toBe('/api/attachments/g1');
    } finally { vi.unstubAllGlobals(); }
  });

  it('draws the background this device remembers behind the messages, under them', async () => {
    localStorage.setItem('gom-pattern', 'lotus');
    try {
      open({}, [msg(1, 'minh')]);
      await flush();
      const backdrop = root.querySelector<HTMLElement>('.thread-pane .backdrop')!;
      expect(backdrop.dataset['pattern']).toBe('lotus');
      expect(backdrop.querySelector('.msgs [data-msg="id1"]')).not.toBeNull();
    } finally { localStorage.removeItem('gom-pattern'); }
  });

  it('plays a file the server found to be sound in the message, and keeps every other file a card to save', async () => {
    const m: ChatMessage = { ...msg(1, 'minh', '', at(10)), attachments: [{ ...att('f1', 'file', 'bai-hat.mp3'), audio: true }, att('f2', 'file', 'loi-bai-hat.mp3')] };
    open({}, [m]);
    await flush();
    const bubble = root.querySelector('[data-msg="id1"] .bub')!;
    expect(bubble.querySelector('[data-audio="f1"] audio')!.getAttribute('src')).toBe('/api/attachments/f1?play=true');
    expect(bubble.querySelector('[data-audio="f2"]')).toBeNull();   // named .mp3, but not sound to the server: a card, as before
    expect(bubble.querySelector('a.filecard[data-file="f2"]')).not.toBeNull();
  });

  it('warns about an app file on its card and asks before saving it; an ordinary file has neither', async () => {
    const m: ChatMessage = { ...msg(1, 'minh', '', at(10)), attachments: [att('f1', 'file', 'thiep_tet.APK'), att('f2', 'file', 'ghi chú.txt')] };
    open({}, [m]);
    await flush();
    const bubble = root.querySelector('[data-msg="id1"] .bub')!;
    expect(bubble.querySelectorAll('[data-app-file-warning]')).toHaveLength(1);
    const plain = bubble.querySelector<HTMLAnchorElement>('[data-file="f2"]')!;
    expect(plain.hasAttribute('data-app-file')).toBe(false);

    const saved: string[] = [];
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) { saved.push(this.getAttribute('href')!); });
    const card = bubble.querySelector<HTMLAnchorElement>('[data-file="f1"]')!;
    const tap = new MouseEvent('click', { bubbles: true, cancelable: true });
    card.dispatchEvent(tap);
    await flush();
    expect(tap.defaultPrevented).toBe(true);
    expect(text(root.querySelector('[data-app-file-panel]')!)).toContain('thiep_tet.APK');
    root.querySelector<HTMLButtonElement>('[data-app-file-no]')!.click();
    await flush();
    expect(root.querySelector('[data-app-file-panel]')).toBeNull();
    expect(saved).toEqual([]);

    card.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
    await flush();
    root.querySelector<HTMLButtonElement>('[data-app-file-yes]')!.click();
    await flush();
    expect(saved).toEqual(['/api/attachments/f1']);
    expect(root.querySelector('[data-app-file-panel]')).toBeNull();
    click.mockRestore();
  });

  describe('the picture viewer', () => {
    const viewer = () => root.querySelector<HTMLElement>('[data-viewer]');
    const count = () => text(viewer()!.querySelector('[data-viewer-count]')!);
    const current = () => viewer()!.querySelector('[data-viewer-download]')!.getAttribute('href');
    const press = (key: string) => viewer()!.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
    const click = async (selector: string) => { root.querySelector<HTMLButtonElement>(selector)!.click(); await flush(); await flush(); };
    const minh = { id: 'u-minh', displayName: 'Minh' };
    const listed = (id: string, seq: number, body = `m${seq}`) => ({ attachment: att(id), messageId: `id${seq}`, seq, createdAt: at(10, seq), sender: minh, body });

    it('is a dialog that steps through the pictures of a message it cannot find in the list, without going round, and closes with Escape', async () => {
      open({}, [{ ...msg(1, 'minh', 'ảnh', at(10)), attachments: [att('p1'), att('p2'), att('p3')] }]);
      await flush();
      await click('[data-pic="p2"]');
      expect(viewer()!.tagName).toBe('DIALOG');
      expect(viewer()!.querySelectorAll('[data-viewer-slide]')).toHaveLength(3);
      expect(current()).toBe('/api/attachments/p2');
      expect(count()).toBe('2 / 3');
      await click('[data-viewer-next]');
      expect(current()).toBe('/api/attachments/p3');
      expect(root.querySelector('[data-viewer-next]')).toBeNull();   // the newest picture: nothing further
      press('ArrowRight'); await flush(); await flush();
      expect(current()).toBe('/api/attachments/p3');   // and the arrow key does not go round to the first
      press('ArrowLeft'); await flush(); await flush();
      expect(current()).toBe('/api/attachments/p2');
      press('Escape'); await flush();
      expect(viewer()).toBeNull();
    });

    it('goes on to the pictures of other messages, oldest on the left, with who sent each, when, and its words', async () => {
      const media = vi.fn(async () => ok({ items: [listed('p3', 2, 'cả nhà'), listed('p2', 2, 'cả nhà'), listed('p1', 1, 'hôm qua')], hasMore: false }));
      open({ media }, [{ ...msg(1, 'minh', 'hôm qua'), attachments: [att('p1')] }, { ...msg(2, 'minh', 'cả nhà'), attachments: [att('p3'), att('p2')] }]);
      await flush();
      await click('[data-pic="p3"]');
      expect(media).toHaveBeenCalledWith(C1, 'image', undefined);
      expect([...viewer()!.querySelectorAll('[data-viewer-slide]')].map((e) => e.getAttribute('data-viewer-slide'))).toEqual(['p1', 'p3', 'p2']);
      expect(count()).toBe('2 / 3');
      expect(text(viewer()!.querySelector('[data-viewer-foot]')!)).toContain('Minh');
      expect(text(viewer()!.querySelector('[data-viewer-caption]')!)).toBe('cả nhà');
      await click('[data-viewer-prev]');
      expect(current()).toBe('/api/attachments/p1');
      expect(text(viewer()!.querySelector('[data-viewer-caption]')!)).toBe('hôm qua');
      expect(root.querySelector('[data-viewer-prev]')).toBeNull();
    });

    it('asks for older pictures at the oldest one it has, and steps back into them', async () => {
      const media = vi.fn(async (_c: string, _k: string, before?: number) => ok(before === undefined ? { items: [listed('p5', 5)], hasMore: true } : { items: [listed('p4', 4)], hasMore: false }));
      open({ media }, [{ ...msg(5, 'minh'), attachments: [att('p5')] }]);
      await flush();
      await click('[data-pic="p5"]');
      expect(count()).toBe('1 / 1+');
      await click('[data-viewer-prev]');
      expect(media).toHaveBeenLastCalledWith(C1, 'image', 5);
      expect(current()).toBe('/api/attachments/p4');
      expect(count()).toBe('1 / 2');
    });

    it('opens from a picture in the side panel instead of jumping to its message', async () => {
      localStorage.setItem('gom-info', '1');
      try {
        open({ media: vi.fn(async (_c: string, kind: string) => ok({ items: kind === 'image' ? [listed('p9', 9)] : [], hasMore: false })) }, [msg(1, 'minh')]);
        await flush();
        await click('[data-info-pic="p9"]');
        expect(current()).toBe('/api/attachments/p9');
        expect(text(viewer()!.querySelector('[data-viewer-foot]')!)).toContain('Minh');
      } finally { localStorage.removeItem('gom-info'); }
    });

    it('follows a swipe: the picture the strip comes to rest on is the current one', async () => {
      const h = open({ media: vi.fn(async () => ok({ items: [listed('p2', 2), listed('p1', 1)], hasMore: false })) }, [{ ...msg(1, 'minh'), attachments: [att('p1')] }, { ...msg(2, 'minh'), attachments: [att('p2')] }]);
      await flush();
      await click('[data-pic="p2"]');
      const track = viewer()!.querySelector<HTMLElement>('[data-viewer-track]')!;
      Object.defineProperty(track, 'clientWidth', { value: 400, configurable: true });
      track.scrollLeft = 0;
      track.dispatchEvent(new Event('scrollend'));
      track.dispatchEvent(new Event('scroll'));
      h.timers.filter((t) => t.ms === 150).at(-1)?.fn();
      await flush();
      expect(current()).toBe('/api/attachments/p1');
    });

    it('moves the strip at once on an arrow step, without a sliding animation, even where motion is allowed', async () => {
      const motion = vi.fn(() => ({ matches: true }));
      vi.stubGlobal('matchMedia', motion);
      try {
        open({ media: vi.fn(async () => ok({ items: [listed('p2', 2), listed('p1', 1)], hasMore: false })) }, [{ ...msg(1, 'minh'), attachments: [att('p1')] }, { ...msg(2, 'minh'), attachments: [att('p2')] }]);
        await flush();
        await click('[data-pic="p1"]');
        const track = viewer()!.querySelector<HTMLElement>('[data-viewer-track]')!;
        Object.defineProperty(track, 'clientWidth', { value: 400, configurable: true });
        const slide = vi.fn();
        track.scrollTo = slide as unknown as HTMLElement['scrollTo'];
        track.scrollLeft = 0;
        await click('[data-viewer-next]');
        expect(current()).toBe('/api/attachments/p2');
        expect(track.scrollLeft).toBe(400);
        expect(slide).not.toHaveBeenCalled();
      } finally { vi.unstubAllGlobals(); }
    });

    it('closes when the browser closes the dialog (Escape or the Back gesture on a phone), and the conversation stays open', async () => {
      const h = open({}, [{ ...msg(1, 'minh'), attachments: [att('p1')] }]);
      await flush();
      await click('[data-pic="p1"]');
      viewer()!.dispatchEvent(new Event('close'));
      await flush();
      expect(viewer()).toBeNull();
      expect(root.querySelector('[data-msg="id1"]')).not.toBeNull();
      expect(h.nav.push).not.toHaveBeenCalled();
    });

    it('has a strip of every picture small, the current one marked, and a click on one goes to it', async () => {
      const media = vi.fn(async () => ok({ items: [listed('p3', 2), listed('p2', 2), listed('p1', 1)], hasMore: false }));
      open({ media }, [{ ...msg(1, 'minh'), attachments: [att('p1')] }, { ...msg(2, 'minh'), attachments: [att('p3'), att('p2')] }]);
      await flush();
      await click('[data-pic="p2"]');
      const thumbs = () => [...viewer()!.querySelectorAll<HTMLButtonElement>('[data-viewer-thumb]')];
      expect(thumbs().map((b) => b.dataset['viewerThumb'])).toEqual(['p1', 'p3', 'p2']);   // oldest on the left, as the pictures above
      expect(thumbs()[0]!.querySelector('img')!.getAttribute('src')).toBe('/api/attachments/p1/thumb');
      expect(thumbs().map((b) => b.getAttribute('aria-current'))).toEqual([null, null, 'true']);
      expect(thumbs()[0]!.getAttribute('aria-label')).toBe('1 / 3');
      await click('[data-viewer-thumb="p1"]');
      expect(current()).toBe('/api/attachments/p1');
      expect(count()).toBe('1 / 3');
      expect(thumbs().map((b) => b.getAttribute('aria-current'))).toEqual(['true', null, null]);
    });

    it('asks for older pictures when the oldest small one is chosen', async () => {
      const media = vi.fn(async (_c: string, _k: string, before?: number) => ok(before === undefined ? { items: [listed('p6', 6), listed('p5', 5)], hasMore: true } : { items: [listed('p4', 4)], hasMore: false }));
      open({ media }, [{ ...msg(5, 'minh'), attachments: [att('p5')] }, { ...msg(6, 'minh'), attachments: [att('p6')] }]);
      await flush();
      await click('[data-pic="p6"]');
      await click('[data-viewer-thumb="p5"]');
      expect(media).toHaveBeenLastCalledWith(C1, 'image', 5);
      expect(count()).toBe('2 / 3');
      expect(viewer()!.querySelector('[data-viewer-thumb="p4"]')).not.toBeNull();
    });

    it('closes with the button, and has no arrows and no strip for a single picture', async () => {
      open({}, [{ ...msg(1, 'minh', '', at(10)), attachments: [att('p1')] }]);
      await flush();
      await click('[data-pic="p1"]');
      expect(root.querySelector('[data-viewer-next]')).toBeNull();
      expect(root.querySelector('[data-viewer-prev]')).toBeNull();
      expect(viewer()!.querySelector('[data-viewer-thumbs]')).toBeNull();
      expect(viewer()!.querySelector('[data-viewer-caption]')).toBeNull();   // no words, no caption
      await click('[data-viewer-close]');
      expect(viewer()).toBeNull();
    });

    it('passes on just the picture shown, without the words', async () => {
      const copyAttachment = vi.fn(async (_c: string, from: string) => ok(att(`copy-${from}`)));
      const send = vi.fn(async (c: string, clientId: string, body: string) => ok({ ...msg(9, 'mom', body, at(11), c), clientId }));
      start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh'), chat(C2, 'Lan')], history: { [C1]: [{ ...msg(1, 'minh', 'hai ảnh'), attachments: [att('p1'), att('p2')] }] }, api: { copyAttachment, send } });
      await flush();
      await click('[data-pic="p2"]');
      await click('[data-viewer-forward]');
      expect(viewer()).toBeNull();
      const box = root.querySelector<HTMLInputElement>(`[data-forward-pick="${C2}"]`)!;
      box.checked = true; box.dispatchEvent(new Event('change', { bubbles: true }));
      await flush();
      await click('[data-forward-send]');
      expect(copyAttachment.mock.calls).toEqual([[C2, 'p2']]);
      expect(send).toHaveBeenCalledWith(C2, expect.any(String), '', undefined, ['copy-p2']);
    });

    it('goes to the message of the picture', async () => {
      open({}, [{ ...msg(1, 'minh'), attachments: [att('p1')] }, msg(2, 'mom')]);
      await flush();
      await click('[data-pic="p1"]');
      await click('[data-viewer-goto]');
      expect(viewer()).toBeNull();
      expect(root.querySelector('[data-msg="id1"]')!.classList.contains('found')).toBe(true);
    });

    it('stays inside a message still being sent, with no way to pass it on yet, and does not look for it on the server', async () => {
      const h = open({ upload: vi.fn(async () => ok(att('p1'))), send: vi.fn(() => new Promise(() => undefined)) });
      await flush();
      await choose([photo()]);
      submitForm();
      await flush();
      await click('[data-msg="new-1"] [data-pic="p1"]');
      expect(current()).toBe('/api/attachments/p1');
      expect(root.querySelector('[data-viewer-forward]')).toBeNull();
      expect(root.querySelector('[data-viewer-goto]')).toBeNull();
      expect(h.api.media).not.toHaveBeenCalled();
    });

    it('closes when the message of the picture shown is taken back', async () => {
      const h = open({}, [{ ...msg(1, 'minh'), attachments: [att('p1')] }]);
      await flush();
      await click('[data-pic="p1"]');
      h.handlers().event(C1, { seq: 2, kind: 'deleted', message: { ...msg(1, 'minh', ''), deleted: true } });
      await flush();
      expect(viewer()).toBeNull();
    });
    it('closes when you delete the message of the picture shown for yourself', async () => {
      open({}, [{ ...msg(1, 'minh'), attachments: [att('p1')] }]);
      await flush();
      root.querySelector<HTMLButtonElement>('[data-msg="id1"] button[data-menu]')!.click(); await flush();
      root.querySelector<HTMLButtonElement>('button[data-hide]')!.click(); await flush();
      await click('[data-pic="p1"]');
      root.querySelector<HTMLButtonElement>('[data-confirm-hide]')!.click();
      await flush(); await flush();
      expect(viewer()).toBeNull();
    });

    it('shows the new words when the message of the picture is changed', async () => {
      const h = open({ media: vi.fn(async () => ok({ items: [listed('p1', 1, 'cũ')], hasMore: false })) }, [{ ...msg(1, 'minh', 'cũ'), attachments: [att('p1')] }]);
      await flush();
      await click('[data-pic="p1"]');
      h.handlers().event(C1, { seq: 2, kind: 'updated', message: { ...msg(1, 'minh', 'mới'), attachments: [att('p1')], editedAt: at(11) } });
      await flush();
      expect(text(viewer()!.querySelector('[data-viewer-caption]')!)).toBe('mới');
    });
  });

  describe('"Lưu về máy" in the menu', () => {
    const menuOf = async (id: string) => { root.querySelector<HTMLButtonElement>(`[data-msg="${id}"] button[data-menu]`)!.click(); await flush(); };

    it('downloads the one picture or file of a message', async () => {
      open({}, [{ ...msg(1, 'minh'), attachments: [att('f1', 'file', 'bảng.xlsx')] }]);
      await flush();
      await menuOf('id1');
      const save = root.querySelector<HTMLAnchorElement>('[data-save]')!;
      expect(save.getAttribute('href')).toBe('/api/attachments/f1');
      expect(save.getAttribute('download')).toBe('bảng.xlsx');
    });

    it('is not offered for a message with several, or none', async () => {
      open({}, [{ ...msg(1, 'minh'), attachments: [att('p1'), att('p2')] }, msg(2, 'minh')]);
      await flush();
      await menuOf('id1');
      expect(root.querySelector('[data-save]')).toBeNull();
      root.querySelector<HTMLButtonElement>('.close-menu')!.click();
      await flush();
      await menuOf('id2');
      expect(root.querySelector('[data-save]')).toBeNull();
    });
  });

  describe('pictures and files the owner trimmed', () => {
    const gone = (a: Attachment): Attachment => ({ ...a, removed: true });
    const menuOf = async (id: string) => { root.querySelector<HTMLButtonElement>(`[data-msg="${id}"] button[data-menu]`)!.click(); await flush(); };

    it('shows a placeholder in their place, with nothing to open or download', async () => {
      open({}, [{ ...msg(1, 'minh', ''), attachments: [gone(att('p1')), att('p2'), gone(att('f1', 'file', 'bảng.xlsx'))] }]);
      await flush();
      const bubble = root.querySelector('[data-msg="id1"] .bub')!;
      expect(text(bubble.querySelector('[data-pic-removed="p1"]')!)).toBe('Ảnh không còn khả dụng');
      expect(bubble.querySelector('[data-pic="p1"], img[src*="p1"]')).toBeNull();
      expect(bubble.querySelector('[data-pic="p2"]')).not.toBeNull();
      const file = bubble.querySelector('[data-file-removed="f1"]')!;
      expect(text(file)).toContain('Tệp không còn khả dụng');
      expect(text(file)).toContain('bảng.xlsx');
      expect(file.tagName).toBe('DIV');
      expect(bubble.querySelector('a[href="/api/attachments/f1"]')).toBeNull();
    });

    it('offers neither saving nor passing on a message whose only file is gone, but passes on the words and the rest', async () => {
      const h = open({ conversations: vi.fn(async () => ok([chat(C1, 'Minh', { lastSeq: 2 }), chat(C2, 'Lan')])) }, [
        { ...msg(1, 'minh', ''), attachments: [gone(att('f1', 'file'))] },
        { ...msg(2, 'minh', 'xem nè'), attachments: [gone(att('p1')), att('p2')] },
      ]);
      await flush();
      await menuOf('id1');
      expect(root.querySelector('[data-save]')).toBeNull();
      expect(root.querySelector('[data-forward]')).toBeNull();
      root.querySelector<HTMLButtonElement>('.close-menu')!.click();
      await flush();
      await menuOf('id2');
      root.querySelector<HTMLButtonElement>('button[data-forward]')!.click();
      await flush();
      expect(text(root.querySelector('[data-forward-panel]')!)).toContain('1 ảnh');
      expect(text(root.querySelector('[data-forward-panel]')!)).not.toContain('2 ảnh');
      expect(h.api.copyAttachment).not.toHaveBeenCalled();
    });

    it('leaves a trimmed picture out of the viewer', async () => {
      open({}, [{ ...msg(1, 'minh', ''), attachments: [att('p1'), gone(att('p2')), att('p3')] }]);
      await flush();
      root.querySelector<HTMLButtonElement>('[data-pic="p1"]')!.click();
      await flush(); await flush();
      expect(text(root.querySelector('[data-viewer] [data-viewer-count]')!)).toBe('1 / 2');
    });

    it('asks for the thread and the side lists again when a trim notice arrives, and says what was trimmed', async () => {
      const history: ChatMessage[] = [{ ...msg(1, 'minh', ''), attachments: [att('p1')] }];
      const h = open({}, history);
      await flush();
      expect(h.api.history).toHaveBeenCalledTimes(1);
      const trimmed = { ...msg(2, 'minh', ''), notice: { type: 'filesTrimmed' as const, target: null, before: new Date(2026, 0, 1, 12).toISOString() } };   // noon on 1 January where the test runs, so the day shown is the same in any time zone
      // What the server now says: the picture is a placeholder, and the notice is in the stream.
      history.splice(0, 1, { ...msg(1, 'minh', ''), attachments: [gone(att('p1'))] }, trimmed);
      h.handlers().event(C1, created(trimmed));
      await flush();
      expect(h.api.history).toHaveBeenCalledTimes(2);
      expect(text(root.querySelector('[data-notice]')!)).toBe('Ảnh và tệp gửi trước 01/01/2026 đã được dọn');
      expect(root.querySelector('[data-pic-removed="p1"]')).not.toBeNull();
    });

    it('asks again too when old messages were trimmed, and says so', async () => {
      const history: ChatMessage[] = [msg(1, 'minh', 'cũ'), msg(2, 'minh', 'mới')];
      const h = open({}, history);
      await flush();
      const trimmed = { ...msg(3, 'minh', ''), notice: { type: 'messagesTrimmed' as const, target: null, before: new Date(2026, 0, 1, 12).toISOString() } };
      history.splice(0, 1);   // the server no longer has the old one
      history.push(trimmed);
      h.handlers().event(C1, created(trimmed));
      await flush();
      expect(h.api.history).toHaveBeenCalledTimes(2);
      expect(root.querySelector('[data-msg="id1"]')).toBeNull();
      expect(root.querySelector('[data-msg="id2"]')).not.toBeNull();
      expect(text(root.querySelector('[data-notice]')!)).toBe('Tin nhắn gửi trước 01/01/2026 đã được dọn');
    });

    it('does not ask again for a notice of another kind', async () => {
      const h = open({}, [msg(1, 'minh')]);
      await flush();
      h.handlers().event(C1, created({ ...msg(2, 'minh', ''), notice: { type: 'left', target: null } }));
      await flush();
      expect(h.api.history).toHaveBeenCalledTimes(1);
    });
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

  describe('the side panel list, from the server', () => {
    const item = (id: string, seq: number, kind: 'image' | 'file' = 'image', name = 'a.webp') => ({ attachment: att(id, kind, name), messageId: `m${seq}`, seq, createdAt: at(10), sender: { id: 'u-minh', displayName: 'Minh' }, body: '' });
    const served = (images: ReturnType<typeof item>[], files: ReturnType<typeof item>[], hasMoreImages = false) =>
      vi.fn(async (_c: string, kind: 'image' | 'file', before?: number) =>
        ok({ items: (kind === 'image' ? images : files).filter((i) => before === undefined || i.seq < before), hasMore: kind === 'image' && before === undefined && hasMoreImages }));
    const panel = () => { localStorage.setItem('gom-info', '1'); };
    afterEach(() => localStorage.removeItem('gom-info'));

    it('lists the pictures and files the server names, not only what the thread has loaded', async () => {
      panel();
      const media = served([item('p1', 9)], [item('f1', 8, 'file', 'bảng.xlsx')]);
      open({ media }, [msg(1, 'minh', 'xin chào')]);   // the thread itself has no attachment at all
      await flush();
      expect(media).toHaveBeenCalledWith(C1, 'image', undefined);
      expect(media).toHaveBeenCalledWith(C1, 'file', undefined);
      expect(root.querySelector('[data-info-media] [data-info-pic="p1"]')).not.toBeNull();
      expect(text(root.querySelector('[data-info-files]')!)).toContain('bảng.xlsx');
      expect(root.querySelector('[data-info-more]')).toBeNull();
    });

    it('asks for the next page after the last item shown, and adds it', async () => {
      panel();
      const media = vi.fn(async (_c: string, kind: 'image' | 'file', before?: number) =>
        ok(kind === 'file' ? { items: [], hasMore: false }
          : before === undefined ? { items: [item('p9', 9), item('p8', 8)], hasMore: true } : { items: [item('p7', 7)], hasMore: false }));
      open({ media });
      await flush();
      expect(root.querySelectorAll('[data-info-pic]')).toHaveLength(2);
      root.querySelector<HTMLButtonElement>('[data-info-more="image"]')!.click();
      await flush();
      expect(media).toHaveBeenLastCalledWith(C1, 'image', 8);
      expect([...root.querySelectorAll('[data-info-pic]')].map((e) => e.getAttribute('data-info-pic'))).toEqual(['p9', 'p8', 'p7']);
      expect(root.querySelector('[data-info-more]')).toBeNull();
    });

    it('asks only when the panel is open, and again nothing on a second look', async () => {
      const media = served([item('p1', 9)], []);
      open({ media });
      await flush();
      expect(media).not.toHaveBeenCalled();
      root.querySelector<HTMLButtonElement>('[data-info]')!.click();
      await flush();
      expect(media).toHaveBeenCalledTimes(2);
      root.querySelector<HTMLButtonElement>('[data-info]')!.click();
      root.querySelector<HTMLButtonElement>('[data-info]')!.click();
      await flush();
      expect(media).toHaveBeenCalledTimes(2);
    });

    it('adds the pictures of a new message and drops those of a message taken back, as they happen', async () => {
      panel();
      const h = open({ media: served([item('p1', 1)], []) }, [msg(1, 'minh', 'a')]);
      await flush();
      h.handlers().event(C1, { seq: 2, kind: 'created', message: { ...msg(2, 'minh', ''), attachments: [att('p2')] } });
      await flush();
      expect([...root.querySelectorAll('[data-info-pic]')].map((e) => e.getAttribute('data-info-pic'))).toEqual(['p2', 'p1']);
      h.handlers().event(C1, { seq: 3, kind: 'deleted', message: { ...msg(2, 'minh', ''), deleted: true } });
      await flush();
      expect([...root.querySelectorAll('[data-info-pic]')].map((e) => e.getAttribute('data-info-pic'))).toEqual(['p1']);
    });

    it('shows nothing, and no error, when the list cannot be fetched', async () => {
      panel();
      open({ media: vi.fn(async () => fail('server', 500)) });
      await flush();
      expect(root.querySelector('[data-info-panel]')).not.toBeNull();
      expect(root.querySelector('[data-info-media]')).toBeNull();
      expect(root.querySelector('[data-info-files]')).toBeNull();
      expect(root.querySelector('.error')).toBeNull();
    });
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

describe("chat: someone's card", () => {
  const G = C1;
  const group = chat(G, 'Nhà', { kind: 'group', peer: null, memberCount: 3, lastSeq: 1 });
  const member = (id: string, displayName: string, extra: Partial<import('../Scripts/api.js').GroupMember> = {}) =>
    ({ id, displayName, isMe: false, isFriend: false, gomId: null, request: 'none' as const, role: 'member' as const, ...extra });
  const cardOf = (extra: Partial<import('../Scripts/api.js').PersonCard> = {}) =>
    ({ id: 'u-minh', displayName: 'Minh', gomId: null, friend: 'none' as const, commonGroups: [{ id: G, title: 'Nhà', memberCount: 3 }], ...extra });
  const card = () => root.querySelector<HTMLElement>('[data-person-card]');
  const click = async (selector: string) => { root.querySelector<HTMLElement>(selector)!.click(); await flush(); await flush(); };
  const inGroup = (api: Record<string, unknown> = {}) => start({ path: `/c/${G}`, chats: [group, chat(C2, 'Lan')], history: { [G]: [msg(1, 'minh', 'chào')] }, api });

  it("opens from a sender's name in a group, without their Gom ID, and asks to be friends through that group", async () => {
    let state: 'none' | 'sent' = 'none';
    const person = vi.fn(async () => ok(cardOf({ friend: state })));
    const askMember = vi.fn(async () => { state = 'sent'; return ok({ status: 'pending' }); });
    inGroup({ person, askMember });
    await flush();
    await click('[data-sender="u-minh"]');
    expect(person).toHaveBeenCalledWith('u-minh');
    expect(card()!.tagName).toBe('DIALOG');
    expect(text(card()!)).toContain('Minh');
    expect(text(card()!)).not.toContain('@');
    await click('[data-card-befriend]');
    expect(askMember).toHaveBeenCalledWith(G, 'u-minh');
    expect(card()!.querySelector('[data-card-sent]')).not.toBeNull();
    expect(card()!.querySelector('[data-card-befriend]')).toBeNull();
  });

  it('offers to accept a request they sent', async () => {
    inGroup({ person: vi.fn(async () => ok(cardOf({ friend: 'received' }))) });
    await flush();
    await click('[data-sender="u-minh"]');
    expect(text(card()!.querySelector('[data-card-befriend]')!)).toBe('Chấp nhận kết bạn');
  });

  it('opens from a member of the side panel, but not for yourself', async () => {
    localStorage.setItem('gom-info', '1');
    try {
      const person = vi.fn(async () => ok(cardOf()));
      inGroup({ person, groupMembers: vi.fn(async () => ok([member('u-mom', 'Mẹ', { isMe: true, role: 'leader' }), member('u-minh', 'Minh')])) });
      await flush();
      expect(root.querySelector('[data-open-card="u-mom"]')).toBeNull();
      await click('[data-open-card="u-minh"]');
      expect(person).toHaveBeenCalledWith('u-minh');
      expect(card()).not.toBeNull();
    } finally { localStorage.removeItem('gom-info'); }
  });

  it("a friend's card has their Gom ID, and \"Nhắn tin\" opens your 1:1 conversation", async () => {
    const h = inGroup({ person: vi.fn(async () => ok(cardOf({ gomId: 'minh', friend: 'friends' }))), openDirect: vi.fn(async () => ok({ id: C2 })) });
    await flush();
    await click('[data-sender="u-minh"]');
    expect(text(card()!)).toContain('@minh');
    await click('[data-card-message]');
    expect(h.api.openDirect).toHaveBeenCalledWith('minh');
    expect(h.nav.path).toBe(`/c/${C2}`);
    expect(card()).toBeNull();
  });

  it('asks before unfriending, and does nothing on "Không"', async () => {
    const h = inGroup({ person: vi.fn(async () => ok(cardOf({ gomId: 'minh', friend: 'friends' }))) });
    await flush();
    await click('[data-sender="u-minh"]');
    await click('[data-card-unfriend]');
    expect(text(card()!.querySelector('[data-card-ask]')!)).toContain('Xóa bạn với Minh?');
    await click('[data-card-no]');
    expect(h.api.unfriend).not.toHaveBeenCalled();
    await click('[data-card-unfriend]');
    await click('[data-card-yes]');
    expect(h.api.unfriend).toHaveBeenCalledWith('minh');
  });

  it('asks before blocking', async () => {
    const h = inGroup({ person: vi.fn(async () => ok(cardOf({ gomId: 'minh', friend: 'friends' }))) });
    await flush();
    await click('[data-sender="u-minh"]');
    await click('[data-card-block]');
    expect(text(card()!.querySelector('[data-card-ask]')!)).toContain('Chặn Minh?');
    await click('[data-card-yes]');
    expect(h.api.block).toHaveBeenCalledWith('minh');
  });

  it('offers no unfriending or blocking to someone who is not a friend', async () => {
    inGroup({ person: vi.fn(async () => ok(cardOf())) });
    await flush();
    await click('[data-sender="u-minh"]');
    expect(card()!.querySelector('[data-card-unfriend]')).toBeNull();
    expect(card()!.querySelector('[data-card-block]')).toBeNull();
    expect(card()!.querySelector('[data-card-message]')).toBeNull();
  });

  it('lists the groups you share, and one opens that group', async () => {
    const h = start({ path: `/c/${C2}`, chats: [group, chat(C2, 'Minh')], history: { [C2]: [msg(1, 'minh', 'chào', at(10, 1), C2)], [G]: [] },
      api: { person: vi.fn(async () => ok(cardOf({ gomId: 'minh', friend: 'friends' }))) } });
    await flush();
    await click('[data-peer-card]');
    expect(text(card()!)).toContain('Nhóm chung (1)');
    await click(`[data-card-group="${G}"]`);
    expect(card()).toBeNull();
    expect(h.nav.path).toBe(`/c/${G}`);
  });

  it("a 1:1 conversation's header says how many groups you share", async () => {
    const person = vi.fn(async () => ok(cardOf({ gomId: 'minh', friend: 'friends', commonGroups: [{ id: G, title: 'Nhà', memberCount: 3 }, { id: 'g2', title: 'Lớp', memberCount: 9 }] })));
    start({ path: `/c/${C2}`, chats: [chat(C2, 'Minh')], history: { [C2]: [] }, api: { person } });
    await flush(); await flush();
    expect(person).toHaveBeenCalledWith('u-minh');
    expect(text(root.querySelector('[data-peer-card]')!)).toContain('@minh · 2 nhóm chung');
  });

  it("a 1:1 conversation's header shows no Gom ID once you are no longer friends, and no stray @", async () => {
    const person = vi.fn(async () => ok(cardOf({ gomId: null, friend: 'none', commonGroups: [{ id: G, title: 'Nhà', memberCount: 3 }] })));
    const former = chat(C2, 'Minh', { peer: { id: 'u-minh', gomId: null, displayName: 'Minh' } });
    start({ path: `/c/${C2}`, chats: [former], history: { [C2]: [] }, api: { person } });
    await flush(); await flush();
    const line = text(root.querySelector('[data-peer-card]')!);
    expect(line).toContain('1 nhóm chung');
    expect(line).not.toContain('@');
  });

  it('says so when the card cannot be had, and closes when the browser closes the dialog', async () => {
    inGroup({ person: vi.fn(async () => fail('unknown', 404)) });
    await flush();
    await click('[data-sender="u-minh"]');
    expect(card()!.querySelector('.err, [role="alert"]')).not.toBeNull();
    card()!.dispatchEvent(new Event('close'));
    await flush();
    expect(card()).toBeNull();
  });
});

describe('chat: the side panel of a group', () => {
  const G = C1;
  const group = chat(G, 'Nhà', { kind: 'group', peer: null, memberCount: 2 });
  const me_ = { id: 'u-mom', displayName: 'Mẹ', isMe: true, isFriend: false, gomId: 'mom', request: 'none' as const };
  const minh = { id: 'u-minh', displayName: 'Minh', isMe: false, isFriend: true, gomId: 'minh', request: 'none' as const, role: 'member' as const };
  const openPanel = (role: 'leader' | 'member', api: Record<string, unknown> = {}) => {
    localStorage.setItem('gom-info', '1');
    let left = false;
    return start({ path: `/c/${G}`, history: { [G]: [] }, api: {
      groupMembers: vi.fn(async () => ok([{ ...me_, role }, minh])),
      conversations: vi.fn(async () => ok(left ? [chat(C2, 'Lan')] : [structuredClone(group), chat(C2, 'Lan')])),
      leaveGroup: vi.fn(async () => { left = true; return ok(undefined); }),
      ...api } });
  };
  afterEach(() => localStorage.removeItem('gom-info'));
  const click = async (selector: string) => { root.querySelector<HTMLElement>(selector)!.click(); await flush(); await flush(); };

  it('links to adding members on the group page', async () => {
    openPanel('member');
    await flush();
    expect(root.querySelector('[data-info-add]')!.getAttribute('href')).toBe(`/groups/${G}#add`);
  });

  it('leaves after a question, and goes back to the list', async () => {
    const h = openPanel('member');
    await flush();
    await click('[data-info-leave]');
    expect(h.api.leaveGroup).not.toHaveBeenCalled();
    await click('[data-info-leave-no]');
    expect(root.querySelector('[data-info-leave-ask]')).toBeNull();
    await click('[data-info-leave]');
    await click('[data-info-leave-yes]');
    expect(h.api.leaveGroup).toHaveBeenCalledWith(G);
    expect(h.nav.path).toBe('/');
    expect(root.querySelector(`[data-chat="${G}"]`)).toBeNull();
  });

  it('tells the leader of a group with others in it to hand over first', async () => {
    openPanel('leader');
    await flush();
    expect(root.querySelector<HTMLButtonElement>('[data-info-leave]')!.disabled).toBe(true);
    expect(root.querySelector('[data-info-leader-note]')).not.toBeNull();
  });

  it('says why when leaving is refused', async () => {
    openPanel('member', { leaveGroup: vi.fn(async () => fail('leader_must_hand_over', 409)) });
    await flush();
    await click('[data-info-leave]');
    await click('[data-info-leave-yes]');
    expect(text(root)).toContain('Trưởng nhóm cần chuyển quyền');
  });
});

describe('chat: profile pictures', () => {
  it('shows the picture of the other person of a 1:1 conversation in the list and the header', async () => {
    start({ path: `/c/${C2}`, chats: [chat(C2, 'Minh', { peer: { id: 'u-minh', gomId: 'minh', displayName: 'Minh', avatar: 'av-minh' } })], history: { [C2]: [] } });
    await flush();
    expect(root.querySelector(`[data-chat="${C2}"] .av img`)!.getAttribute('src')).toBe('/api/avatars/av-minh');
    expect(root.querySelector('.th-head .av img')!.getAttribute('src')).toBe('/api/avatars/av-minh');
  });

  it("shows a group member's picture beside their messages, from the group's member list", async () => {
    const groupMembers = vi.fn(async () => ok([{ id: 'u-minh', displayName: 'Minh', avatar: 'av-minh', isMe: false, isFriend: true, gomId: 'minh', request: 'none', role: 'member' }]));
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Nhà', { kind: 'group', peer: null, memberCount: 2, lastSeq: 1 })], history: { [C1]: [msg(1, 'minh', 'chào')] }, api: { groupMembers } });
    await flush(); await flush();
    expect(groupMembers).toHaveBeenCalledWith(C1);
    expect(root.querySelector('[data-msg="id1"] .av img')!.getAttribute('src')).toBe('/api/avatars/av-minh');
  });

  it('shows the initials when there is no picture', async () => {
    start({ path: `/c/${C2}`, chats: [chat(C2, 'Minh')], history: { [C2]: [] } });
    await flush();
    expect(root.querySelector(`[data-chat="${C2}"] .av img`)).toBeNull();
    expect(root.querySelector(`[data-chat="${C2}"] .av`)!.textContent).toBe('M');
  });
});

describe('chat: stickers', () => {
  beforeEach(() => { try { localStorage.clear(); } catch { /* none */ } });
  const sticker = (seq: number, from: 'mom' | 'minh', code: string): ChatMessage => ({ ...msg(seq, from, ''), sticker: code });
  const open = (extra: Record<string, unknown> = {}, messages: ChatMessage[] = [msg(1, 'minh', 'chào mẹ')], chats = [chat(C1, 'Minh', { lastSeq: messages.length }), chat(C2, 'Lan')]) =>
    start({ path: `/c/${C1}`, chats, history: { [C1]: messages }, api: extra });
  const echo = () => vi.fn(async (conversation: string, clientId: string, code: string, replyTo?: string) =>
    ok({ ...sticker(9, 'mom', code), conversationId: conversation, id: `s-${clientId}`, clientId, ...(replyTo ? { reply: { id: replyTo, sender: null, snippet: null } } : {}) }));
  const panel = () => root.querySelector('[data-sticker-panel]');
  const toggle = async () => { root.querySelector<HTMLButtonElement>('[data-picker-toggle]')!.click(); await flush(); };
  const pack = async (index: number) => { root.querySelector<HTMLButtonElement>(`[data-sticker-pack="${index}"]`)!.click(); await flush(); };
  const shown = () => [...root.querySelectorAll('[data-shown-stickers] [data-send-sticker]')].map((b) => b.getAttribute('data-send-sticker'));
  const tap = async (code: string, where = '[data-shown-stickers]') => { root.querySelector<HTMLButtonElement>(`${where} [data-send-sticker="${code}"]`)!.click(); await flush(); await flush(); };

  it('opens a panel of sticker packs from the smiley, on the first pack, with the credit, and closes it again', async () => {
    open();
    await flush();
    expect(panel()).toBeNull();
    await toggle();
    expect(root.querySelector('[data-picker-toggle]')!.getAttribute('aria-expanded')).toBe('true');
    const buttons = [...panel()!.querySelectorAll('[data-sticker-pack]')];
    expect(buttons.map((b) => b.getAttribute('aria-label'))).toEqual(STICKER_PACKS.map((p) => vietnamese.stickerPacks[p.key]));
    expect(buttons[0]!.getAttribute('aria-pressed')).toBe('true');
    expect(shown()).toEqual(STICKER_PACKS[0]!.codes);
    expect(text(panel()!)).toContain('Cảm xúc');
    expect(root.querySelector('[data-send-sticker="1f602"]')!.getAttribute('aria-label')).toBe('Gửi nhãn dán 😂');
    expect(root.querySelector<HTMLImageElement>('[data-send-sticker="1f602"] img')!.getAttribute('src')).toBe('/stickers/1f602.svg');
    expect(text(panel()!)).toContain('Noto Emoji của Google, giấy phép Apache 2.0; cờ các nước thuộc phạm vi công cộng.');
    await toggle();
    expect(panel()).toBeNull();
  });

  it('sends a sticker at once with a tap, shows it as a picture while it goes, and keeps the panel open', async () => {
    let finish: (r: unknown) => void = () => undefined;
    const sendSticker = vi.fn((_c: string, clientId: string, code: string) => new Promise((resolve) => { finish = () => resolve(ok({ ...sticker(2, 'mom', code), clientId })); }));
    open({ sendSticker });
    await flush();
    await toggle();
    await tap('1f602');
    expect(sendSticker).toHaveBeenCalledWith(C1, 'new-1', '1f602', undefined);
    const pending = root.querySelector('[data-msg="new-1"]')!;
    expect(pending.getAttribute('data-state')).toBe('sending');
    expect(pending.classList.contains('sticker')).toBe(true);
    expect(pending.querySelector<HTMLImageElement>('img.stk')!.getAttribute('src')).toBe('/stickers/1f602.svg');
    expect(panel()).not.toBeNull();
    finish(undefined);
    await flush();
    expect(root.querySelector('[data-msg="id2"] img.stk')).not.toBeNull();
  });

  it('puts the stickers sent last at the top of the panel, newest first, on this device', async () => {
    open({ sendSticker: echo() });
    await flush();
    await toggle();
    expect(root.querySelector('[data-sticker-pack="-1"]')).toBeNull();
    await tap('1f602');
    expect(shown()).toEqual(STICKER_PACKS[0]!.codes);   // stays on the pack being chosen from
    await pack(2);
    await tap('2764');
    await pack(0);
    await tap('1f602');
    await pack(-1);
    expect(shown()).toEqual(['1f602', '2764']);
    expect(text(panel()!)).toContain('Gần đây');
    // Opened again, it starts there.
    await toggle(); await toggle();
    expect(root.querySelector('[data-sticker-pack="-1"]')!.getAttribute('aria-pressed')).toBe('true');
  });

  it('shows one pack at a time, each with its own stickers, and every sticker is in one pack', async () => {
    open();
    await flush();
    await toggle();
    for (const [i, p] of STICKER_PACKS.entries()) {
      await pack(i);
      expect(shown(), p.key).toEqual(p.codes);
      expect(text(panel()!.querySelector('h3')!)).toBe(vietnamese.stickerPacks[p.key]);
    }
    expect(STICKER_PACKS.flatMap((p) => p.codes)).toEqual(STICKERS);
  });

  it('closes the panel when the message field is used, or on Escape', async () => {
    open();
    await flush();
    await toggle();
    root.querySelector<HTMLTextAreaElement>('.composer textarea')!.dispatchEvent(new FocusEvent('focus'));
    await flush();
    expect(panel()).toBeNull();
    await toggle();
    panel()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await flush();
    expect(panel()).toBeNull();
  });

  it('shows a sticker it received as a picture without a bubble, with no copy or edit in its menu', async () => {
    open({}, [sticker(1, 'mom', '1f44d')]);
    await flush();
    const row = root.querySelector('[data-msg="id1"]')!;
    expect(row.classList.contains('sticker')).toBe(true);
    expect(row.querySelector('img.stk')!.getAttribute('alt')).toBe('Nhãn dán 👍');
    expect(row.querySelector('.txt')).toBeNull();
    row.querySelector<HTMLButtonElement>('button[data-menu]')!.click();
    await flush();
    expect(root.querySelector('[data-copy]')).toBeNull();
    expect(root.querySelector('[data-edit]')).toBeNull();
    expect(root.querySelector('[data-forward]')).not.toBeNull();
    expect(root.querySelector('[data-recall]')).not.toBeNull();
  });

  it('answers a sticker: the bar and the quote say "Nhãn dán", and a sticker can be the answer', async () => {
    const sendSticker = echo();
    open({ sendSticker }, [sticker(1, 'minh', '1f382'), { ...msg(2, 'mom', 'cảm ơn'), reply: { id: 'id1', sender: { id: 'u-minh', displayName: 'Minh' }, snippet: '', sticker: '1f382' } }]);
    await flush();
    const quote = root.querySelector('[data-msg="id2"] [data-quote]')!;
    expect(text(quote)).toContain('Nhãn dán');
    expect(quote.querySelector('img')!.getAttribute('src')).toBe('/stickers/1f382.svg');

    root.querySelector<HTMLButtonElement>('[data-msg="id1"] [data-quick-reply]')!.click();
    await flush();
    expect(text(root.querySelector('[data-replybar]')!)).toContain('Nhãn dán');
    await toggle();
    await pack(1);   // 👍 is among the gestures
    await tap('1f44d');
    expect(sendSticker).toHaveBeenCalledWith(C1, 'new-1', '1f44d', 'id1');
    expect(root.querySelector('[data-replybar]')).toBeNull();
  });

  it('says "Đã gửi một nhãn dán" in the list for a sticker', async () => {
    const last = sticker(1, 'minh', '1f602');
    start({ chats: [withLast(last, { lastMessage: { seq: 1, body: '', createdAt: last.createdAt, sender: last.sender, sticker: '1f602' } })] });
    await flush();
    expect(text(root.querySelector(`[data-chat="${C1}"] .pv`)!)).toBe('Đã gửi một nhãn dán');
  });

  it('says so in the list too for a sticker that arrives live', async () => {
    const h = start({ chats: [withLast(msg(1, 'minh'))] });
    await flush();
    h.handlers().event(C1, created(sticker(2, 'minh', '1f44b')));
    await flush();
    expect(text(root.querySelector(`[data-chat="${C1}"] .pv`)!)).toBe('Đã gửi một nhãn dán');
  });

  it('passes a sticker on as a sticker', async () => {
    const sendSticker = echo();
    const send = vi.fn();
    open({ sendSticker, send }, [sticker(1, 'minh', '1f389')]);
    await flush();
    root.querySelector<HTMLButtonElement>('[data-msg="id1"] button[data-menu]')!.click(); await flush();
    root.querySelector<HTMLButtonElement>('button[data-forward]')!.click(); await flush();
    expect(root.querySelector('[data-forward-panel] img.fw-stk')!.getAttribute('src')).toBe('/stickers/1f389.svg');
    const box = root.querySelector<HTMLInputElement>(`[data-forward-pick="${C2}"]`)!;
    box.checked = true; box.dispatchEvent(new Event('change', { bubbles: true }));
    await flush();
    root.querySelector<HTMLButtonElement>('[data-forward-send]')!.click();
    await flush(); await flush();
    expect(sendSticker).toHaveBeenCalledWith(C2, expect.any(String), '1f389', undefined);
    expect(send).not.toHaveBeenCalled();
  });

  it('keeps a sticker that could not go in the outbox, and sends it as a sticker after a reload', async () => {
    const h = open({ sendSticker: vi.fn(async () => fail('network', 0)) });
    await flush();
    await toggle();
    await tap('1f62d');
    expect(h.outbox.items).toEqual([{ conversation: C1, clientId: 'new-1', body: '', createdAt: NOW.toISOString(), sticker: '1f62d' }]);

    const again = vi.fn(async (_c: string, clientId: string, code: string) => ok({ ...sticker(2, 'mom', code), clientId }));
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh')], outbox: h.outbox.items, api: { sendSticker: again } });
    await flush(); await flush();
    expect(again).toHaveBeenCalledWith(C1, 'new-1', '1f62d', undefined);
  });

  it('offers no stickers while a message is being edited', async () => {
    open({}, [msg(1, 'mom', 'sửa tôi')]);
    await flush();
    root.querySelector<HTMLButtonElement>('[data-msg="id1"] button[data-menu]')!.click(); await flush();
    root.querySelector<HTMLButtonElement>('button[data-edit]')!.click(); await flush();
    expect(root.querySelector('[data-picker-toggle]')).toBeNull();
  });
});

describe('chat: emoji', () => {
  beforeEach(() => { try { localStorage.clear(); } catch { /* none */ } });
  const open = (messages: ChatMessage[] = [msg(1, 'minh', 'chào mẹ')], extra: Record<string, unknown> = {}) =>
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh', { lastSeq: messages.length })], history: { [C1]: messages }, api: extra });
  const field = () => root.querySelector<HTMLTextAreaElement>('.composer textarea')!;
  const toggle = async () => { root.querySelector<HTMLButtonElement>('[data-picker-toggle]')!.click(); await flush(); };
  const tab = async (name: 'emoji' | 'stickers') => { root.querySelector<HTMLButtonElement>(`[data-tab="${name}"]`)!.click(); await flush(); };

  it('opens on the sticker tab the first time, and on the tab used last after that', async () => {
    open();
    await flush();
    await toggle();
    expect(root.querySelector('[data-tab="stickers"]')!.getAttribute('aria-selected')).toBe('true');
    expect(root.querySelector('[data-sticker-panel]')).not.toBeNull();
    await tab('emoji');
    expect(root.querySelector('[data-emoji-panel]')).not.toBeNull();
    expect(root.querySelector('[data-sticker-panel]')).toBeNull();
    await toggle();
    await toggle();
    expect(root.querySelector('[data-tab="emoji"]')!.getAttribute('aria-selected')).toBe('true');
  });

  it('puts an emoji where the cursor was, keeps the panel open, and sends it with the words', async () => {
    const send = vi.fn(async (_c: string, clientId: string, body: string) => ok({ ...msg(2, 'mom', body), clientId }));
    open(undefined, { send });
    await flush();
    compose('ab');
    field().setSelectionRange(1, 1);
    await toggle();
    await tab('emoji');
    root.querySelector<HTMLButtonElement>('[data-emoji="😀"]')!.click();
    await flush();
    expect(field().value).toBe('a😀b');
    root.querySelector<HTMLButtonElement>('[data-emoji="😂"]')!.click();
    await flush();
    expect(field().value).toBe('a😀😂b');
    expect(root.querySelector('[data-emoji-panel]')).not.toBeNull();
    submitForm();
    await flush();
    expect(send).toHaveBeenCalledWith(C1, 'new-1', 'a😀😂b', undefined);
  });

  it('shows one group at a time, and the ones used lately once there are some', async () => {
    open();
    await flush();
    await toggle();
    await tab('emoji');
    expect(root.querySelector('[data-emoji-group="-1"]')).toBeNull();
    expect(text(root.querySelector('[data-emoji-panel] h3')!)).toBe('Mặt cười');
    root.querySelector<HTMLButtonElement>('[data-emoji-group="8"]')!.click();
    await flush();
    expect(root.querySelector('[data-emoji-group="8"]')!.getAttribute('aria-label')).toBe('Cờ');
    expect(root.querySelector('[data-emoji="🇻🇳"]')).not.toBeNull();
    expect(root.querySelector('[data-emoji="😀"]')).toBeNull();
    root.querySelector<HTMLButtonElement>('[data-emoji="🇻🇳"]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('[data-emoji-group="-1"]')!.click();
    await flush();
    expect([...root.querySelectorAll('[data-emoji-panel] .e-grid [data-emoji]')].map((b) => b.getAttribute('data-emoji'))).toEqual(['🇻🇳']);
  });

  it('draws a message of one to three emoji large without a bubble, and anything more as usual', async () => {
    open([msg(1, 'minh', '😂'), msg(2, 'minh', '❤️ ❤️ ❤️'), msg(3, 'minh', '😂😂😂😂'), msg(4, 'minh', 'ha 😂'), msg(5, 'minh', '123'),
      { ...msg(6, 'minh', '👍'), reply: { id: 'id1', sender: { id: 'u-minh', displayName: 'Minh' }, snippet: '😂' } }]);
    await flush();
    const jumbo = (id: string) => root.querySelector(`[data-msg="${id}"]`)!.classList.contains('jumbo');
    expect([1, 2, 3, 4, 5, 6].map((n) => jumbo(`id${n}`))).toEqual([true, true, false, false, false, false]);
  });
});

describe('chat: someone who deleted their account', () => {
  const gone = chat(C1, 'Tài khoản đã xóa', { peer: { id: 'u-gone', gomId: 'lan', displayName: 'Tài khoản đã xóa', deleted: true }, lastSeq: 1 });

  it('keeps the conversation to read, with a note where the composer was', async () => {
    start({ path: `/c/${C1}`, chats: [gone], history: { [C1]: [msg(1, 'minh', 'tin cũ')] } });
    await flush();
    expect(root.querySelector('[data-msg="id1"]')).not.toBeNull();
    expect(root.querySelector('form.composer')).toBeNull();
    expect(text(root.querySelector('[data-peer-deleted]')!)).toContain('Tài khoản này đã bị xóa');
    expect(text(root.querySelector('.th-title small')!)).toBe('Tài khoản đã bị xóa');
  });

  it('has a card that says so and offers nothing', async () => {
    start({ path: `/c/${C1}`, chats: [gone], history: { [C1]: [] },
      api: { person: vi.fn(async () => ok({ id: 'u-gone', displayName: 'Tài khoản đã xóa', gomId: null, friend: 'none', commonGroups: [], deleted: true })) } });
    await flush();
    root.querySelector<HTMLButtonElement>('[data-peer-card]')!.click();
    await flush(); await flush();
    const card = root.querySelector('[data-person-card]')!;
    expect(card.querySelector('[data-card-deleted]')).not.toBeNull();
    expect(card.querySelector('.pcard-actions')).toBeNull();
  });

  it('still has the composer for everyone else', async () => {
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh')], history: { [C1]: [] } });
    await flush();
    expect(root.querySelector('form.composer')).not.toBeNull();
    expect(root.querySelector('[data-peer-deleted]')).toBeNull();
  });
});

describe('the menu of a conversation in the list', () => {
  const C3 = '33333333-3333-4333-8333-333333333333';
  const openMenu = async (id: string) => { root.querySelector<HTMLButtonElement>(`[data-row-menu="${id}"]`)!.click(); await flush(); };
  const menuEl = () => root.querySelector<HTMLElement>('[data-conversation-menu]');
  const item = (attr: string) => menuEl()!.querySelector<HTMLElement>(`[data-${attr}]`);

  it('every row has a button that opens a menu named after the conversation, focused on its first item, and Escape closes it back to the button', async () => {
    start({ chats: [chat(C1, 'Minh'), chat(C2, 'Lan')] });
    await flush();
    expect(root.querySelectorAll('[data-row-menu]')).toHaveLength(2);
    await openMenu(C2);
    expect(menuEl()!.getAttribute('aria-label')).toBe('Tùy chọn cho Lan');
    expect(root.querySelector(`[data-row-menu="${C2}"]`)!.getAttribute('aria-expanded')).toBe('true');
    expect(document.activeElement).toBe(menuEl()!.querySelector('[role="menuitem"]'));
    menuEl()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await flush();
    expect(menuEl()).toBeNull();
    expect(document.activeElement).toBe(root.querySelector(`[data-row-menu="${C2}"]`));
  });

  it('right-click on a row opens the same menu instead of the browser\'s', async () => {
    start({ chats: [chat(C1, 'Minh')] });
    await flush();
    const e = new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 50, clientY: 60 });
    rows()[0]!.dispatchEvent(e);
    await flush();
    expect(e.defaultPrevented).toBe(true);
    expect(menuEl()).not.toBeNull();
  });

  it('pins and unpins, then shows the list as the server now has it, with a pin mark', async () => {
    const h = start({ chats: [chat(C1, 'Minh'), chat(C2, 'Lan')] });
    await flush();
    h.api.conversations.mockImplementation(async () => ok([chat(C2, 'Lan', { pinned: true }), chat(C1, 'Minh')]));
    await openMenu(C2);
    item('pin-chat')!.click();
    await flush();
    expect(h.api.setPinned).toHaveBeenCalledWith(C2, true);
    expect(menuEl()).toBeNull();
    expect(rows()[0]!.getAttribute('data-chat')).toBe(C2);
    expect(rows()[0]!.querySelector('.pin-mark')!.getAttribute('aria-label')).toBe('Đã ghim');
    await openMenu(C2);
    item('unpin-chat')!.click();
    await flush();
    expect(h.api.setPinned).toHaveBeenLastCalledWith(C2, false);
  });

  it('says so in words when no more can be pinned', async () => {
    const h = start({ chats: [chat(C1, 'Minh')], api: { setPinned: vi.fn(async () => fail('pin_limit', 409)) } });
    await flush();
    await openMenu(C1);
    item('pin-chat')!.click();
    await flush();
    expect(h.api.setPinned).toHaveBeenCalled();
    expect(text(root)).toContain('Bạn đã ghim tối đa số cuộc trò chuyện.');
  });

  it('marks a read conversation unread with a dot and no number, and opening it reads it even with nothing new', async () => {
    const m = msg(3, 'minh');
    const h = start({ chats: [withLast(m, { lastReadSeq: 3 })], history: { [C1]: [m] } });
    await flush();
    await openMenu(C1);
    expect(item('mark-read')).toBeNull();
    h.api.conversations.mockImplementation(async () => ok([withLast(m, { lastReadSeq: 3, markedUnread: true })]));
    item('mark-unread')!.click();
    await flush();
    expect(h.api.setMarkedUnread).toHaveBeenCalledWith(C1, true);
    const dot = rows()[0]!.querySelector('[data-marked-unread]')!;
    expect(dot.getAttribute('aria-label')).toBe('Đánh dấu chưa đọc');
    expect(dot.textContent).toBe('');
    expect(rows()[0]!.classList.contains('unread')).toBe(true);

    rows()[0]!.click();
    await flush(); await flush();
    expect(h.api.markRead).toHaveBeenCalledWith(C1, 3);
    expect(rows()[0]!.querySelector('[data-marked-unread]')).toBeNull();
  });

  it('reads a conversation marked unread when it is opened, even one with nothing in it yet', async () => {
    const h = start({ chats: [chat(C1, 'Minh', { markedUnread: true })], history: { [C1]: [] } });
    await flush();
    rows()[0]!.click();
    await flush(); await flush();
    expect(h.api.markRead).toHaveBeenCalledWith(C1, 0);
  });

  it('offers "mark as read" for a conversation with unread messages or a mark, which reads up to its end', async () => {
    const h = start({ chats: [chat(C1, 'Minh', { lastSeq: 7, lastReadSeq: 2, unread: 5 }), chat(C2, 'Lan', { lastSeq: 4, lastReadSeq: 4, markedUnread: true })] });
    await flush();
    await openMenu(C1);
    expect(item('mark-unread')).toBeNull();
    item('mark-read')!.click();
    await flush();
    expect(h.api.markRead).toHaveBeenCalledWith(C1, 7);
    await openMenu(C2);
    item('mark-read')!.click();
    await flush();
    expect(h.api.markRead).toHaveBeenLastCalledWith(C2, 4);
  });

  it('mutes through the same choices as the bell, and unmutes at once', async () => {
    const h = start({ chats: [chat(C1, 'Minh'), chat(C2, 'Lan', { muted: true })] });
    await flush();
    await openMenu(C1);
    item('mute-chat')!.click();
    await flush();
    expect(menuEl()).toBeNull();
    root.querySelector<HTMLButtonElement>('[data-mute-panel] [data-mute-for="always"]')!.click();
    await flush();
    expect(h.api.setMute).toHaveBeenCalledWith(C1, true, undefined);
    await openMenu(C2);
    item('unmute-chat')!.click();
    await flush();
    expect(h.api.setMute).toHaveBeenLastCalledWith(C2, false, undefined);
  });

  it('offers a group with a friend from a one-to-one chat only, with that friend ticked on the new-group page', async () => {
    const group: Conversation = { ...chat(C2, 'Nhà'), kind: 'group', peer: null };
    const gone = chat(C3, 'Cũ', { peer: { id: 'u-old', gomId: 'old', displayName: 'Cũ', deleted: true } });
    start({ chats: [chat(C1, 'Minh'), group, gone] });
    await flush();
    await openMenu(C1);
    expect(item('group-with')!.getAttribute('href')).toBe('/groups/new?with=minh');
    expect(item('group-with')!.textContent).toContain('Tạo nhóm với Minh');
    await openMenu(C2);
    expect(item('group-with')).toBeNull();
    await openMenu(C3);
    expect(item('group-with')).toBeNull();
  });

  it('hides a conversation: it leaves the list, and an open one closes', async () => {
    const h = start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh'), chat(C2, 'Lan')], history: { [C1]: [msg(1, 'minh')] } });
    await flush();
    await openMenu(C1);
    item('hide-chat')!.click();
    await flush();
    expect(h.api.setHidden).toHaveBeenCalledWith(C1, true);
    expect(h.nav.push).toHaveBeenCalledWith('/');
    expect(root.querySelector('form.composer')).toBeNull();
  });

  it('keeps a conversation the server marks hidden out of the list, and something new said in it brings it back', async () => {
    const h = start({ chats: [chat(C1, 'Minh'), chat(C2, 'Lan', { hidden: true, lastSeq: 1 })] });
    await flush();
    expect(rows().map((r) => r.getAttribute('data-chat'))).toEqual([C1]);
    h.handlers().event(C2, created({ ...msg(2, 'minh', '', at(11), C2), notice: { type: 'left', target: null } }));
    await flush();
    expect(rows().map((r) => r.getAttribute('data-chat'))).toEqual([C1]);   // a notice does not bring it back
    h.handlers().event(C2, created(msg(3, 'minh', 'Mẹ ơi', at(11), C2)));
    await flush();
    expect(rows().map((r) => r.getAttribute('data-chat'))).toEqual([C2, C1]);
  });

  it('offers "Hoàn tác" after hiding, which shows it again', async () => {
    const h = start({ chats: [chat(C1, 'Minh'), chat(C2, 'Lan')] });
    await flush();
    h.api.conversations.mockImplementation(async () => ok([chat(C1, 'Minh', { hidden: true }), chat(C2, 'Lan')]));
    await openMenu(C1);
    item('hide-chat')!.click();
    await flush();
    expect(rows().map((r) => r.getAttribute('data-chat'))).toEqual([C2]);
    expect(text(root.querySelector('[data-undo-hide]')!)).toContain('Đã ẩn cuộc trò chuyện');
    h.api.conversations.mockImplementation(async () => ok([chat(C1, 'Minh'), chat(C2, 'Lan')]));
    root.querySelector<HTMLButtonElement>('[data-undo]')!.click();
    await flush();
    expect(h.api.setHidden).toHaveBeenLastCalledWith(C1, false);
    expect(root.querySelector('[data-undo-hide]')).toBeNull();
    expect(rows().map((r) => r.getAttribute('data-chat'))).toEqual([C1, C2]);
  });

  it('takes "Hoàn tác" away after a few seconds', async () => {
    const h = start({ chats: [chat(C1, 'Minh')] });
    await flush();
    await openMenu(C1);
    item('hide-chat')!.click();
    await flush();
    const offer = h.timers.filter((t) => t.ms === 5000 && t.live);
    expect(offer).toHaveLength(1);
    offer[0]!.fn();
    await flush();
    expect(root.querySelector('[data-undo-hide]')).toBeNull();
  });

  it('offers no "Hoàn tác" when hiding failed', async () => {
    start({ chats: [chat(C1, 'Minh')], api: { setHidden: vi.fn(async () => fail('not_allowed', 403)) } });
    await flush();
    await openMenu(C1);
    item('hide-chat')!.click();
    await flush();
    expect(root.querySelector('[data-undo-hide]')).toBeNull();
  });

  it('finds a hidden conversation by search, under "Đã ẩn", and only while something is typed', async () => {
    start({ chats: [chat(C1, 'Minh'), chat(C2, 'Lan', { hidden: true }), chat(C3, 'Lâm', { hidden: true })] });
    await flush();
    expect(root.querySelector('[data-hidden-label]')).toBeNull();
    fill(root, 'input[name="filter"]', 'lan');
    await flush();
    expect(text(root.querySelector('[data-hidden-label]')!)).toBe('Đã ẩn');
    expect([...root.querySelectorAll('[data-hidden-list] [data-chat]')].map((r) => r.getAttribute('data-chat'))).toEqual([C2]);
    expect(root.querySelector('[data-no-match]')).toBeNull();
    fill(root, 'input[name="filter"]', 'zzz');
    await flush();
    expect(root.querySelector('[data-no-match]')).not.toBeNull();
  });

  it('opening a hidden conversation found by search brings it back to the list, here and on the server', async () => {
    const h = start({ chats: [chat(C1, 'Minh'), chat(C2, 'Lan', { hidden: true })], history: { [C2]: [msg(1, 'minh', 'xin chào', at(10), C2)] } });
    await flush();
    fill(root, 'input[name="filter"]', 'lan');
    await flush();
    root.querySelector<HTMLElement>(`[data-hidden-list] [data-chat="${C2}"]`)!.click();
    await flush(); await flush();
    expect(h.api.setHidden).toHaveBeenCalledWith(C2, false);
    expect(text(root)).toContain('xin chào');
    fill(root, 'input[name="filter"]', '');
    await flush();
    expect(rows().map((r) => r.getAttribute('data-chat'))).toContain(C2);
    expect(root.querySelector('[data-hidden-label]')).toBeNull();
  });

  it('opening a hidden conversation by its address brings it back too (the card and Back go the same way)', async () => {
    const h = start({ path: `/c/${C2}`, chats: [chat(C1, 'Minh'), chat(C2, 'Lan', { hidden: true })], history: { [C2]: [msg(1, 'minh', 'xin chào', at(10), C2)] } });
    await flush(); await flush();
    expect(h.api.setHidden).toHaveBeenCalledWith(C2, false);
    expect(text(root)).not.toContain('Không tìm thấy cuộc trò chuyện này.');
    expect(rows().map((r) => r.getAttribute('data-chat'))).toEqual([C1, C2]);
  });

  it('says it is not found, and unhides nothing, for a conversation you are not in', async () => {
    const h = start({ path: `/c/${C3}`, chats: [chat(C1, 'Minh')] });
    await flush(); await flush();
    expect(text(root)).toContain('Không tìm thấy cuộc trò chuyện này.');
    expect(h.api.setHidden).not.toHaveBeenCalled();
  });

  it('offers "Bỏ ẩn" in the menu of a hidden one found by search', async () => {
    const h = start({ chats: [chat(C1, 'Minh'), chat(C2, 'Lan', { hidden: true })] });
    await flush();
    fill(root, 'input[name="filter"]', 'lan');
    await flush();
    await openMenu(C2);
    expect(item('hide-chat')).toBeNull();
    item('unhide-chat')!.click();
    await flush();
    expect(h.api.setHidden).toHaveBeenCalledWith(C2, false);
    expect(h.api.conversations).toHaveBeenCalledTimes(2);
  });

  it('shows the empty list when every conversation is hidden', async () => {
    start({ chats: [chat(C1, 'Minh', { hidden: true })] });
    await flush();
    expect(rows()).toHaveLength(0);
    expect(text(root)).toContain('Chưa có cuộc trò chuyện nào');
  });

  it('asks before deleting a conversation for you, and a no changes nothing', async () => {
    const h = start({ chats: [chat(C1, 'Minh')] });
    await flush();
    await openMenu(C1);
    item('clear-chat')!.click();
    await flush();
    expect(text(menuEl()!)).toContain('Xóa toàn bộ tin nhắn của cuộc trò chuyện này ở phía bạn? Người khác vẫn thấy chúng.');
    expect(document.activeElement).toBe(menuEl()!.querySelector('[data-confirm-clear]'));
    menuEl()!.querySelector<HTMLButtonElement>('[data-cancel-clear]')!.click();
    await flush();
    expect(h.api.clearConversation).not.toHaveBeenCalled();
    expect(rows()).toHaveLength(1);
  });

  it('deletes the conversation for you: its messages are gone from this page, and opening it again asks the server', async () => {
    const h = start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh')], history: { [C1]: [msg(1, 'minh', 'bí mật')] } });
    await flush();
    expect(text(root)).toContain('bí mật');
    await openMenu(C1);
    item('clear-chat')!.click();
    await flush();
    menuEl()!.querySelector<HTMLButtonElement>('[data-confirm-clear]')!.click();
    await flush();
    expect(h.api.clearConversation).toHaveBeenCalledWith(C1);
    expect(h.nav.push).toHaveBeenCalledWith('/');
    expect(text(root)).not.toContain('bí mật');
    const asked = h.api.history.mock.calls.length;
    h.handlers().conversations();
    await flush();
    h.nav.path = `/c/${C1}`; h.nav.pop();
    await flush();
    expect(h.api.history.mock.calls.length).toBe(asked + 1);
  });

  it('a "cleared" frame from another device drops what this page holds and asks again for an open thread', async () => {
    const h = start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh')], history: { [C1]: [msg(1, 'minh', 'bí mật')] } });
    await flush();
    h.api.history.mockImplementation(async () => ok({ messages: [], hasMore: false, lastSeq: 1 }));
    h.handlers().cleared(C1, 1);
    await flush();
    expect(text(root)).not.toContain('bí mật');
  });
});

describe('quieter menus', () => {
  const pointer = (type: string, init: { pointerType?: string; clientX?: number; clientY?: number } = {}) =>
    Object.assign(new Event(type, { bubbles: true, cancelable: true }), { pointerType: init.pointerType ?? 'touch', clientX: init.clientX ?? 100, clientY: init.clientY ?? 100 });
  const menuEl = () => root.querySelector('[data-conversation-menu]');

  it('a finger held on a row for a second opens its menu, and the click that ends it does not open the conversation', async () => {
    const h = start({ chats: [chat(C1, 'Minh')] });
    await flush();
    rows()[0]!.dispatchEvent(pointer('pointerdown'));
    const timer = h.timers[h.timers.length - 1]!;
    expect(timer.ms).toBe(1000);
    expect(menuEl()).toBeNull();
    timer.fn();
    await flush();
    expect(menuEl()).not.toBeNull();
    rows()[0]!.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
    await flush();
    expect(h.nav.push).not.toHaveBeenCalled();
    // The next tap opens it as usual.
    root.querySelector<HTMLElement>('[data-close-row-menu]')!.click();
    rows()[0]!.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
    await flush();
    expect(h.nav.push).toHaveBeenCalledWith(`/c/${C1}`);
  });

  it('a finger lifted early, or moving on to scroll, opens nothing; a mouse press is not a long press', async () => {
    const h = start({ chats: [chat(C1, 'Minh')] });
    await flush();
    rows()[0]!.dispatchEvent(pointer('pointerdown'));
    let timer = h.timers[h.timers.length - 1]!;
    rows()[0]!.dispatchEvent(pointer('pointerup'));
    timer.fn();
    rows()[0]!.dispatchEvent(pointer('pointerdown', { clientX: 100, clientY: 100 }));
    timer = h.timers[h.timers.length - 1]!;
    rows()[0]!.dispatchEvent(pointer('pointermove', { clientX: 100, clientY: 140 }));
    timer.fn();
    const before = h.timers.length;
    rows()[0]!.dispatchEvent(pointer('pointerdown', { pointerType: 'mouse' }));
    expect(h.timers.length).toBe(before);
    await flush();
    expect(menuEl()).toBeNull();
  });

  it('on a phone the browser\'s own long-press menu is held back, and right-click opens nothing extra', async () => {
    const h = start({ chats: [chat(C1, 'Minh')] });
    h.coarse.value = true;
    await flush();
    const e = new MouseEvent('contextmenu', { bubbles: true, cancelable: true });
    rows()[0]!.dispatchEvent(e);
    await flush();
    expect(e.defaultPrevented).toBe(true);
    expect(menuEl()).toBeNull();
  });

  it('a press outside the emoji and sticker panel closes it; a press inside it or on its button does not', async () => {
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh')], history: { [C1]: [msg(1, 'minh')] } });
    await flush();
    root.querySelector<HTMLButtonElement>('[data-picker-toggle]')!.click();
    await flush();
    expect(root.querySelector('[data-picker]')).not.toBeNull();
    root.querySelector('[data-picker]')!.dispatchEvent(pointer('pointerdown'));
    root.querySelector('[data-picker-toggle]')!.dispatchEvent(pointer('pointerdown'));
    await flush();
    expect(root.querySelector('[data-picker]')).not.toBeNull();
    root.querySelector('.msgs')!.dispatchEvent(pointer('pointerdown'));
    await flush();
    expect(root.querySelector('[data-picker]')).toBeNull();
  });
});

describe('chat: the information page on a phone', () => {
  const G = '33333333-3333-4333-8333-333333333333';
  const minhCard = { id: 'u-minh', displayName: 'Minh', gomId: 'minh', friend: 'friends' as const, commonGroups: [] };
  const open = async (h = start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh')], history: { [C1]: [msg(1, 'minh')] }, api: { person: vi.fn(async () => ok(minhCard)) } })) => {
    await flush();
    root.querySelector<HTMLButtonElement>('[data-info-page-open]')!.click();
    await flush();
    return h;
  };
  const page = () => root.querySelector<HTMLElement>('[data-info-page]');

  it('keeps room in the header: search, mute and the group\'s page are marked for wide screens only, and an info button opens the page', async () => {
    start({ path: `/c/${G}`, chats: [{ id: G, kind: 'group', title: 'Nhà', peer: null, lastSeq: 0, lastReadSeq: 0, unread: 0, lastMessage: null, memberCount: 2 }], api: { groupMembers: vi.fn(async () => ok([])) } });
    await flush();
    const head = root.querySelector('.th-head')!;
    expect(head.querySelector('[data-search]')!.classList.contains('wide')).toBe(true);
    expect(head.querySelector('[data-mute]')!.classList.contains('wide')).toBe(true);
    expect(head.querySelector(`a[href="/groups/${G}"]`)!.classList.contains('wide')).toBe(true);
    expect(head.querySelector('[data-info-page-open]')).not.toBeNull();
  });

  it('opens as a step in the history, shows search and mute, and the back button steps back to the thread', async () => {
    const h = await open();
    expect(page()).not.toBeNull();
    expect(text(page()!)).toContain('Thông tin');
    expect(text(page()!)).toContain('Minh');
    expect(page()!.querySelector('[data-info-search]')).not.toBeNull();
    expect(page()!.querySelector('[data-info-mute]')).not.toBeNull();
    expect(h.nav.push).toHaveBeenLastCalledWith(`/c/${C1}#info`);
    page()!.querySelector<HTMLButtonElement>('[data-info-page-close]')!.click();
    await flush();
    expect(h.nav.back).toHaveBeenCalledOnce();
    expect(page()).toBeNull();
    expect(root.querySelector('.msgs')).not.toBeNull();
  });

  it('closes on the back gesture too', async () => {
    const h = await open();
    h.nav.back();
    await flush();
    expect(page()).toBeNull();
    expect(h.nav.path).toBe(`/c/${C1}`);
  });

  it('searching from the page closes it and opens the search in the thread, without leaving a step to come back to', async () => {
    const h = await open();
    page()!.querySelector<HTMLButtonElement>('[data-info-search]')!.click();
    await flush();
    expect(page()).toBeNull();
    expect(root.querySelector('.searchbar input')).not.toBeNull();
    expect(h.nav.history).toEqual([`/c/${C1}`, `/c/${C1}`]);   // the page's step became the conversation again
  });

  it('going to a pinned message from the page closes it and shows the message, without leaving a step to come back to', async () => {
    const pinned = msg(1, 'minh');
    const h = await open(start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh')], history: { [C1]: [pinned, msg(2, 'mom')] },
      api: { person: vi.fn(async () => ok(minhCard)), pins: vi.fn(async () => ok({ pins: [pinned] })) } }));
    page()!.querySelector<HTMLButtonElement>(`[data-info-pin="${pinned.id}"]`)!.click();
    await flush();
    expect(page()).toBeNull();
    expect(root.querySelector(`[data-msg="${pinned.id}"]`)).not.toBeNull();
    expect(h.nav.history).toEqual([`/c/${C1}`, `/c/${C1}`]);
  });

  it('going to the message of a picture opened from the page closes the page too', async () => {
    const picture: Attachment = { id: 'p1', kind: 'image', name: 'a.webp', size: 2048, width: 40, height: 30 };
    const shown = { ...msg(1, 'minh'), attachments: [picture] };
    const listed = { attachment: picture, messageId: shown.id, seq: 1, createdAt: shown.createdAt, sender: shown.sender, body: shown.body };
    await open(start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh')], history: { [C1]: [shown, msg(2, 'mom')] },
      api: { person: vi.fn(async () => ok(minhCard)), media: vi.fn(async (_c: string, kind: string) => ok({ items: kind === 'image' ? [listed] : [], hasMore: false })) } }));
    page()!.querySelector<HTMLButtonElement>('[data-info-pic="p1"]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('[data-viewer-goto]')!.click();
    await flush();
    expect(page()).toBeNull();
    expect(root.querySelector(`[data-msg="${shown.id}"]`)!.classList.contains('found')).toBe(true);
  });

  it('hides the conversation from its foot, back to the list with the undo', async () => {
    const h = await open();
    page()!.querySelector<HTMLButtonElement>('[data-info-hide]')!.click();
    await flush();
    expect(h.api.setHidden).toHaveBeenCalledWith(C1, true);
    expect(page()).toBeNull();
    expect(root.querySelector('[data-undo-hide]')).not.toBeNull();
  });

  it('clears the conversation only after its question, and Hủy keeps it', async () => {
    const h = await open();
    page()!.querySelector<HTMLButtonElement>('[data-info-clear]')!.click();
    await flush();
    expect(text(page()!.querySelector('[data-info-clear-ask]')!)).toContain('Xóa toàn bộ tin nhắn của cuộc trò chuyện này ở phía bạn?');
    page()!.querySelector<HTMLButtonElement>('[data-info-clear-no]')!.click();
    await flush();
    expect(h.api.clearConversation).not.toHaveBeenCalled();
    page()!.querySelector<HTMLButtonElement>('[data-info-clear]')!.click();
    await flush();
    page()!.querySelector<HTMLButtonElement>('[data-info-clear-yes]')!.click();
    await flush();
    expect(h.api.clearConversation).toHaveBeenCalledWith(C1);
    expect(page()).toBeNull();
  });

  it('blocks through the person\'s card, which asks first', async () => {
    await open();
    page()!.querySelector<HTMLButtonElement>('[data-info-block]')!.click();
    await flush(); await flush();
    const card = root.querySelector<HTMLElement>('[data-person-card]')!;
    expect(text(card)).toContain('Chặn Minh?');
  });

  it('offers no block in a group, where leaving is the way out', async () => {
    start({ path: `/c/${G}`, chats: [{ id: G, kind: 'group', title: 'Nhà', peer: null, lastSeq: 0, lastReadSeq: 0, unread: 0, lastMessage: null, memberCount: 2 }], api: { groupMembers: vi.fn(async () => ok([])) } });
    await flush();
    root.querySelector<HTMLButtonElement>('[data-info-page-open]')!.click();
    await flush();
    expect(page()!.querySelector('[data-info-block]')).toBeNull();
    expect(page()!.querySelector('[data-info-hide]')).not.toBeNull();
    expect(page()!.querySelector('[data-info-leave]')).not.toBeNull();
  });
});

describe('chat: an account still waiting to be let in', () => {
  it('shows only the waiting screen: no list asked for and no live connection', async () => {
    const h = start({ api: { me: vi.fn(async () => ok({ ...me, standing: 'pending' })) } });
    await flush();
    expect(root.querySelector('[data-waiting="pending"]')).not.toBeNull();
    expect(h.api.conversations).not.toHaveBeenCalled();
    expect(h.live.start).not.toHaveBeenCalled();
  });
});

describe('chat: a reload by itself (a new version)', () => {
  const base = [msg(1, 'minh', 'tin của Minh'), msg(2, 'mom', 'tin của mẹ')];
  const open = (extra: Record<string, unknown> = {}, drafts?: Map<string, string>) =>
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh', { lastSeq: 2 })], history: { [C1]: base }, api: extra, ...(drafts ? { drafts } : {}) });
  const field = () => root.querySelector<HTMLTextAreaElement>('.composer textarea')!;

  it('keeps words typed and not sent, for this person, and puts them back after the reload', async () => {
    const h = open();
    await flush();
    compose('Con đang gõ dở');
    expect(h.drafts.get(`mom:${C1}`)).toBe('Con đang gõ dở');
    // The reload: a new page on the same tab's storage.
    root = document.createElement('div');
    document.body.replaceChildren(root);
    open({}, h.drafts);
    await flush();
    expect(field().value).toBe('Con đang gõ dở');
  });

  it('forgets the words once they are sent', async () => {
    const send = vi.fn(async (c: string, clientId: string, body: string) => ok({ ...msg(3, 'mom', body, at(10), c), clientId }));
    const h = open({ send });
    await flush();
    compose('gửi luôn');
    submitForm();
    await flush();
    expect(send).toHaveBeenCalled();
    expect(h.drafts.has(`mom:${C1}`)).toBe(false);
  });

  it('does not keep the words of a sent message being changed, and holds the reload then', async () => {
    const h = open();
    await flush();
    expect(h.busy()).toBe(false);
    root.querySelector(`[data-msg="id2"]`)!.querySelector<HTMLButtonElement>('button[data-menu]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('button[data-edit]')!.click();
    await flush();
    compose('tin của mẹ, sửa');
    expect(h.drafts.has(`mom:${C1}`)).toBe(false);
    expect(h.busy()).toBe(true);
  });

  it('holds the reload while files are picked and not sent', async () => {
    const h = open({ upload: vi.fn(async () => ok({ id: 'p1', kind: 'image', name: 'a.jpg', size: 3, width: 40, height: 30 } as Attachment)) });
    await flush();
    const input = root.querySelector<HTMLInputElement>('input[name="files"]')!;
    Object.defineProperty(input, 'files', { value: [new File([new Uint8Array([1])], 'a.jpg', { type: 'image/jpeg' })], configurable: true });
    input.dispatchEvent(new Event('change', { bubbles: true }));
    await flush();
    expect(h.busy()).toBe(true);
    root.querySelector<HTMLButtonElement>('[data-tray-remove]')!.click();
    await flush();
    expect(h.busy()).toBe(false);
  });
});

describe('call lines (mockups 29 and 30)', () => {
  const callMsg = (seq: number, from: 'mom' | 'minh', outcome: 'completed' | 'missed' | 'declined' | 'busy' | 'failed', seconds = 0): ChatMessage => ({
    ...msg(seq, from, ''),
    notice: { type: 'call', target: null, call: { callerId: from === 'mom' ? 'u-mom' : 'u-minh', outcome, startedAt: at(8, 12), seconds } },
  });

  it('draws each call as a card on the side of whoever called, worded for the reader, a missed one marked', async () => {
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh')], history: { [C1]: [callMsg(1, 'minh', 'completed', 185), callMsg(2, 'mom', 'missed'), callMsg(3, 'minh', 'missed')] } });
    await flush();
    const cards = [...root.querySelectorAll<HTMLElement>('[data-call-line]')];
    expect(cards.map((c) => [c.classList.contains('out') ? 'out' : 'in', text(c.querySelector('b')!)])).toEqual([
      ['in', 'Cuộc gọi thoại'], ['out', 'Không trả lời'], ['in', 'Cuộc gọi nhỡ'],
    ]);
    expect(text(cards[0]!)).toContain(`3:05 · ${clock(at(8, 12))}`);
    expect(text(cards[0]!)).toContain('Cuộc gọi đến');   // which way, for a screen reader
    expect(cards.map((c) => c.querySelector('.call-card')!.classList.contains('missed'))).toEqual([false, false, true]);
    expect(root.querySelector('[data-call-line] button')).toBeNull();   // no "Gọi lại" where calls are off
  });

  const fakeCalls = (ready = true) => ({ ready: vi.fn(() => ready), start: vi.fn(async () => undefined), frame: vi.fn(), busy: vi.fn(() => false), leaving: vi.fn() });

  it('a friend\'s chat has a call button, and a missed call "Gọi lại", where this page takes calls', async () => {
    const calls = fakeCalls();
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh')], history: { [C1]: [callMsg(1, 'minh', 'missed'), callMsg(2, 'minh', 'completed', 60)] }, calls, callsOn: true });
    await flush();
    root.querySelector<HTMLButtonElement>('[data-call-start]')!.click();
    expect(calls.start).toHaveBeenCalledWith(C1, { name: 'Minh', avatar: undefined });
    expect(root.querySelectorAll('[data-call-back]')).toHaveLength(1);   // only on the missed one
    root.querySelector<HTMLButtonElement>('[data-call-back]')!.click();
    expect(calls.start).toHaveBeenCalledTimes(2);
  });

  it('no call button in a group, with someone no longer a friend, when the server has calls off, on a page that takes none, or in an app that makes none', async () => {
    const cases: Array<[Conversation, boolean, 'ready' | 'none' | 'not ready']> = [
      [chat(C1, 'Nhà', { kind: 'group', peer: null, memberCount: 3 }), true, 'ready'],
      [chat(C1, 'Minh', { peer: { id: 'u-minh', gomId: null, displayName: 'Minh' } }), true, 'ready'],
      [chat(C1, 'Minh'), false, 'ready'],
      [chat(C1, 'Minh'), true, 'none'],
      [chat(C1, 'Minh'), true, 'not ready'],
    ];
    for (const [c, on, here] of cases) {
      root = document.createElement('div');
      document.body.replaceChildren(root);
      start({ path: `/c/${C1}`, chats: [c], history: { [C1]: [callMsg(1, 'minh', 'missed')] }, callsOn: on, ...(here === 'none' ? {} : { calls: fakeCalls(here === 'ready') }) });
      await flush();
      expect(root.querySelector('[data-call-start]')).toBeNull();
      expect(root.querySelector('[data-call-back]')).toBeNull();
    }
  });

  it('hands call frames to the calls, and holds a reload while a call is on', async () => {
    const calls = fakeCalls();
    const h = start({ chats: [chat(C1, 'Minh')], calls, callsOn: true });
    await flush();
    const frame = { kind: 'ended' as const, id: 'k', c: C1, outcome: 'missed' };
    h.handlers().call!(frame);
    expect(calls.frame).toHaveBeenCalledWith(frame);
    expect(h.busy()).toBe(false);
    calls.busy.mockReturnValue(true);
    expect(h.busy()).toBe(true);
  });

  it('shows a call as the last thing in the list, a missed one in red', async () => {
    const missed = callMsg(4, 'minh', 'missed');
    start({ chats: [chat(C1, 'Minh', { lastSeq: 4, unread: 1, lastMessage: { seq: 4, body: '', createdAt: missed.createdAt, sender: missed.sender, call: missed.notice!.call } })] });
    await flush();
    const pv = rows()[0]!.querySelector('.pv')!;
    expect(text(pv)).toBe('Cuộc gọi nhỡ');
    expect(pv.classList.contains('missed-call')).toBe(true);
  });

  it('moves a chat up and counts it unread when a missed call arrives, but not for a call the reader made', async () => {
    const h = start({ chats: [chat(C2, 'An', { lastSeq: 1, lastMessage: { seq: 1, body: 'hi', createdAt: at(11), sender: { id: 'u-an', displayName: 'An' } } }), chat(C1, 'Minh')] });
    await flush();
    h.handlers().event(C1, created(callMsg(1, 'minh', 'missed')));
    await flush();
    expect(rows()[0]!.dataset['chat']).toBe(C1);
    expect(text(rows()[0]!.querySelector('.badge')!)).toBe('1');
    h.handlers().event(C1, created(callMsg(2, 'mom', 'missed')));
    await flush();
    expect(text(rows()[0]!.querySelector('.pv')!)).toBe('Không trả lời');
    expect(text(rows()[0]!.querySelector('.badge')!)).toBe('1');
  });
});

describe('chat: voice messages', () => {
  const sound = new Blob([new Uint8Array([82, 73, 70, 70])], { type: 'audio/wav' });
  const voiceAtt = (id: string): Attachment => ({ id, kind: 'file', name: 'Ghi âm.wav', size: 4, audio: true });
  /** A recorder that hands back `sound` when stopped, and says whether it was stopped or thrown away. */
  const recorder = () => {
    const r = { stopped: 0, cancelled: 0, stop: vi.fn(async () => { r.stopped++; return sound as Blob | null; }), cancel: vi.fn(() => { r.cancelled++; }) };
    return r;
  };
  const open = (recordVoice: ChatDeps['recordVoice'], extra: Record<string, unknown> = {}) =>
    start({ path: `/c/${C1}`, chats: [chat(C1, 'Minh'), chat(C2, 'Lan')], history: { [C1]: [], [C2]: [] }, api: extra, ...(recordVoice ? { recordVoice } : {}) });
  const mic = () => root.querySelector<HTMLButtonElement>('.composer [data-voice]');
  const bar = () => root.querySelector<HTMLElement>('[data-recording]');
  const tick = (h: Harness, times = 1) => { for (let i = 0; i < times; i++) h.timers.filter((t) => t.live && t.ms === 1000).at(-1)!.fn(); };

  it('offers the microphone in the send button’s place while there is nothing to send, and the send button once there are words', async () => {
    open(vi.fn());
    await flush();
    expect(mic()!.getAttribute('aria-label')).toBe('Ghi âm tin nhắn thoại');
    expect(root.querySelector('.composer button[type="submit"]')).toBeNull();
    compose('chào');
    await flush();
    expect(mic()).toBeNull();
    expect(root.querySelector('.composer button[type="submit"]')).not.toBeNull();
    compose('');
    await flush();
    expect(mic()).not.toBeNull();
  });

  it('has no microphone where this browser cannot record', async () => {
    open(undefined);
    await flush();
    expect(mic()).toBeNull();
    expect(root.querySelector('.composer button[type="submit"]')).not.toBeNull();
  });

  it('records with a running time out of five minutes, then sends it as a WAV file on its own once it is up', async () => {
    const r = recorder();
    const upload = vi.fn(async () => ok(voiceAtt('v1')));
    const send = vi.fn(async (c: string, clientId: string, body: string) => ok({ ...msg(1, 'mom', body, at(10), c), clientId }));
    const h = open(vi.fn(async () => r), { upload, send });
    await flush();
    mic()!.click();
    await flush();
    expect(text(bar()!.querySelector('[data-voice-time]')!)).toBe('0:00 / 5:00');
    tick(h, 12);
    await flush();
    expect(text(bar()!.querySelector('[data-voice-time]')!)).toBe('0:12 / 5:00');
    expect(h.busy()).toBe(true);   // a reload would lose the recording
    bar()!.querySelector<HTMLButtonElement>('[data-voice-send]')!.click();
    await flush();
    expect(r.stopped).toBe(1);
    const file = (upload.mock.calls[0] as unknown[])[1] as File;
    expect(file.type).toBe('audio/wav');
    expect(file.name).toMatch(/^Ghi âm \d{4}-\d\d-\d\d \d\d\.\d\d\.\d\d\.wav$/);
    expect(send).toHaveBeenCalledWith(C1, 'new-1', '', undefined, ['v1']);
    expect(bar()).toBeNull();
    expect(root.querySelectorAll('[data-tray-item]')).toHaveLength(0);
    expect(h.timers.filter((t) => t.live && t.ms === 1000)).toHaveLength(0);
  });

  it('stops and sends by itself at five minutes', async () => {
    const r = recorder();
    const send = vi.fn(async (c: string, clientId: string, body: string) => ok({ ...msg(1, 'mom', body, at(10), c), clientId }));
    const h = open(vi.fn(async () => r), { upload: vi.fn(async () => ok(voiceAtt('v1'))), send });
    await flush();
    mic()!.click();
    await flush();
    tick(h, 299);
    await flush();
    expect(r.stopped).toBe(0);
    tick(h);
    await flush();
    expect(r.stopped).toBe(1);
    expect(send).toHaveBeenCalledWith(C1, 'new-1', '', undefined, ['v1']);
  });

  it('throws a recording away without sending anything', async () => {
    const r = recorder();
    const upload = vi.fn();
    const h = open(vi.fn(async () => r), { upload });
    await flush();
    mic()!.click();
    await flush();
    bar()!.querySelector<HTMLButtonElement>('[data-voice-cancel]')!.click();
    await flush();
    expect(r.cancelled).toBe(1);
    expect(upload).not.toHaveBeenCalled();
    expect(bar()).toBeNull();
    expect(mic()).not.toBeNull();
    expect(h.timers.filter((t) => t.live && t.ms === 1000)).toHaveLength(0);
  });

  it('throws the recording away when another conversation is opened', async () => {
    const r = recorder();
    const h = open(vi.fn(async () => r));
    await flush();
    mic()!.click();
    await flush();
    h.nav.path = `/c/${C2}`;
    h.nav.pop();
    await flush();
    expect(r.cancelled).toBe(1);
    expect(bar()).toBeNull();
  });

  it('says in words when the microphone is not allowed', async () => {
    open(vi.fn(async () => 'denied' as const));
    await flush();
    mic()!.click();
    await flush();
    expect(bar()).toBeNull();
    expect(text(root)).toContain('Gom chưa được dùng micrô');
  });

  it('says in words when this browser cannot record', async () => {
    open(vi.fn(async () => 'unsupported' as const));
    await flush();
    mic()!.click();
    await flush();
    expect(text(root)).toContain('Trình duyệt này không ghi âm được.');
  });

  it('keeps a voice message that failed to go up in the tray, to try again, and sends it once it is up', async () => {
    const upload = vi.fn().mockResolvedValueOnce(fail('too_large', 413)).mockResolvedValueOnce(ok(voiceAtt('v1')));
    const send = vi.fn(async (c: string, clientId: string, body: string) => ok({ ...msg(1, 'mom', body, at(10), c), clientId }));
    open(vi.fn(async () => recorder()), { upload, send });
    await flush();
    mic()!.click();
    await flush();
    bar()!.querySelector<HTMLButtonElement>('[data-voice-send]')!.click();
    await flush();
    expect(send).not.toHaveBeenCalled();
    expect(root.querySelector('[data-tray-item] [role="alert"]')).not.toBeNull();
    root.querySelector<HTMLButtonElement>('[data-tray-retry]')!.click();
    await flush();
    expect(send).toHaveBeenCalledWith(C1, 'new-1', '', undefined, ['v1']);
  });
});
