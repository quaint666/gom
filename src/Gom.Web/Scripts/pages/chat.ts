import { html, nothing, render, type TemplateResult } from 'lit-html';
import { live as liveValue } from 'lit-html/directives/live.js';
import { repeat } from 'lit-html/directives/repeat.js';
import type { Api, ChatEvent, ChatMessage, Conversation, Failure, User } from '../api.js';
import { buildThread, clock, initials, listTime, mergeMessages, type Shown, type ThreadItem } from '../chat/model.js';
import type { LiveControl, LiveHandlers } from '../chat/live.js';
import * as icons from '../icons.js';
import { renderedScheme, toggleScheme } from '../theme.js';
import { appBar, errorBox } from '../ui.js';
import { errorText } from '../text.js';

/** Everything the page takes from its surroundings, so a test can stand in for each. */
export interface ChatDeps {
  live(handlers: LiveHandlers): LiveControl;
  nav: { path(): string; push(path: string): void; onPop(callback: () => void): void };
  now(): Date;
  newId(): string;
  /** A phone or tablet: Enter makes a new line there, because the send button is for sending. */
  coarsePointer(): boolean;
  visible(): boolean;
  timer: { set(fn: () => void, ms: number): number; clear(id: number): void };
  /** Messages written but not yet delivered, kept for this person across a reload. */
  outbox(gomId: string): { load(): OutboxItem[]; save(items: OutboxItem[]): void };
}

export interface OutboxItem { conversation: string; clientId: string; body: string; createdAt: string }

interface Thread {
  messages: ChatMessage[];
  pending: Shown[];
  hasMore: boolean;
  loading: boolean;
  loaded: boolean;
  /** What you had read, and how many were new, when you opened it: the "new messages" divider. */
  lastReadAtOpen: number;
  unreadAtOpen: number;
  draft: string;
  stick: boolean;
  /** True while one message of this conversation is on its way: they go out one at a time, in the order written. */
  flushing: boolean;
  retryTimer: number | null;
  /** Failed attempts in a row, for the wait before the next one. */
  failures: number;
  /** Attempts per message that the server (not the network) turned away. */
  attempts: Map<string, number>;
}

const PREVIEW_CHARS = 200;
const CONVERSATION_PATH = /^\/c\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i;
const GAP_NEAR_BOTTOM_PX = 80;
/** A busy or failing server is tried this many times before the message is called failed. A bad network is waited out for ever. */
const MAX_SERVER_TRIES = 5;
const MAX_RETRY_WAIT_MS = 30_000;

/** The signed-in start page: the list of conversations and, beside it (or instead of it on a phone), the open thread. */
export function mountChat(root: HTMLElement, api: Api, go: (path: string) => void, deps: ChatDeps): void {
  let me: User | null = null;
  let chats: Conversation[] | null = null;
  let openId: string | null = idFromPath();
  const threads = new Map<string, Thread>();
  let error: Failure | null = null;
  let notice = '';
  let offline = false;
  let outbox: ReturnType<ChatDeps['outbox']> | null = null;
  let live: LiveControl | null = null;
  let scrollAnchor: number | null = null;

  function idFromPath(): string | null { return CONVERSATION_PATH.exec(deps.nav.path())?.[1]?.toLowerCase() ?? null; }
  const chatOf = (id: string | null): Conversation | undefined => (id === null ? undefined : chats?.find((c) => c.id === id));
  const newestSeq = (t: Thread): number => t.messages[t.messages.length - 1]?.seq ?? 0;

  function threadOf(id: string): Thread {
    let t = threads.get(id);
    if (t === undefined) {
      t = { messages: [], pending: [], hasMore: false, loading: false, loaded: false, lastReadAtOpen: 0, unreadAtOpen: 0, draft: '', stick: true, flushing: false, retryTimer: null, failures: 0, attempts: new Map() };
      threads.set(id, t);
    }
    return t;
  }

  // ---- drawing

  const themeButton = () => html`
    <button class="ib" type="button" aria-label=${renderedScheme() === 'dark' ? 'Chuyển sang giao diện sáng' : 'Chuyển sang giao diện tối'}
      @click=${() => { toggleScheme(); update(); }}>${renderedScheme() === 'dark' ? icons.sun() : icons.moon()}</button>`;

  const avatar = (name: string): TemplateResult => html`<div class="av a1" aria-hidden="true">${initials(name)}</div>`;

  function preview(c: Conversation): string {
    const last = c.lastMessage;
    if (last === null) return 'Chưa có tin nhắn';
    return (last.sender.gomId === me?.gomId ? 'Bạn: ' : '') + last.body;
  }

  const row = (c: Conversation) => html`
    <li><a class="item ${c.unread > 0 ? 'unread' : ''} ${c.id === openId ? 'active' : ''}" href="/c/${c.id}" data-chat=${c.id}
      aria-current=${c.id === openId ? 'page' : nothing} @click=${(e: MouseEvent) => onRowClick(e, c.id)}>
      ${avatar(c.title ?? '?')}
      <div class="im">
        <div class="l1"><span class="nm">${c.title ?? '…'}</span>${c.lastMessage ? html`<span class="tm">${listTime(c.lastMessage.createdAt, deps.now())}</span>` : nothing}</div>
        <div class="l2"><span class="pv">${preview(c)}</span>
          ${c.unread > 0 ? html`<span class="badge" aria-label="${c.unread} tin chưa đọc">${c.unread > 99 ? '99+' : c.unread}</span>` : nothing}</div>
      </div></a></li>`;

  const banner = () => offline ? html`<div class="warn" role="status">${icons.wifiOff()}<span>Mất kết nối. Đang kết nối lại…</span></div>` : nothing;

  const empty = () => html`
    <div class="empty">
      <div class="big-icon">${icons.logo()}</div>
      <h2>Chưa có cuộc trò chuyện nào</h2>
      <p class="lead">Hãy kết bạn rồi bắt đầu trò chuyện.</p>
      <a class="btn" href="/friends">Thêm bạn bè</a>
    </div>`;

  const bubble = (item: Extract<ThreadItem, { kind: 'msg' }>): TemplateResult => {
    const m = item.msg;
    const classes = `mrow ${item.mine ? 'out' : 'in'} ${item.first ? 'first' : ''} ${item.last ? 'last' : ''}`;
    return html`
      <div class=${classes} data-msg=${m.id} data-state=${m.state ?? 'sent'}>
        <div class="bub"><span class="txt">${m.body}</span>
          <span class="ts">${m.state === 'sending' || m.state === 'queued' ? html`<span class="sending" role="img" aria-label=${m.state === 'sending' ? 'Đang gửi' : 'Chờ gửi'}>${icons.clock()}</span>` : clock(m.createdAt)}</span></div>
      </div>
      ${m.state === 'queued' ? html`<div class="queued-note">${m.error?.error === 'network' || m.error === undefined ? 'Sẽ gửi khi có mạng' : 'Sẽ thử gửi lại'}</div>` : nothing}
      ${m.state === 'failed' ? html`
        <div class="failed" role="alert">${icons.alert()}<span>${m.error ? errorText(m.error) : 'Chưa gửi được.'}</span>
          <button class="btn secondary small" type="button" data-retry @click=${() => retry(m.conversationId, m.clientId)}>Gửi lại</button>
          <button class="btn secondary small" type="button" data-discard @click=${() => discard(m.conversationId, m.clientId)}>Xóa</button></div>` : nothing}`;
  };

  const threadItem = (item: ThreadItem) => {
    if (item.kind === 'day') return html`<div class="day">${item.label}</div>`;
    if (item.kind === 'new') return html`<div class="newdiv"><span>${item.count} tin nhắn mới</span></div>`;
    return bubble(item);
  };

  function threadPane(): TemplateResult {
    const c = chatOf(openId);
    if (openId === null) return html`<section class="thread-pane none"><div class="empty"><p class="lead">Chọn một cuộc trò chuyện</p></div></section>`;
    if (chats !== null && c === undefined) return html`
      <section class="thread-pane">
        <header class="th-head"><a class="ib back" href="/" aria-label="Quay lại" @click=${onBack}>${icons.back()}</a></header>
        <div class="empty"><p class="lead">Không tìm thấy cuộc trò chuyện này.</p></div>
      </section>`;
    const t = threadOf(openId);
    const items = buildThread(t.messages, me?.gomId ?? '', t.lastReadAtOpen, t.unreadAtOpen, deps.now(), t.pending);
    return html`
      <section class="thread-pane">
        <header class="th-head">
          <a class="ib back" href="/" aria-label="Quay lại" @click=${onBack}>${icons.back()}</a>
          ${avatar(c?.title ?? '?')}
          <div class="th-title"><b>${c?.title ?? '…'}</b>${c?.peer ? html`<small>@${c.peer.gomId}</small>` : nothing}</div>
        </header>
        ${banner()}
        <div class="msgs" @scroll=${(e: Event) => onScroll(openId!, e.target as HTMLElement)}>
          ${t.hasMore ? html`<button class="btn secondary small more" type="button" data-older ?disabled=${t.loading} @click=${() => void loadOlder(openId!)}>Tải tin cũ hơn</button>` : nothing}
          ${repeat(items, (i) => i.key, threadItem)}
        </div>
        <form class="composer" @submit=${(e: Event) => { e.preventDefault(); void send(openId!); }}>
          <textarea name="message" rows="1" aria-label="Nhập tin nhắn" placeholder="Nhập tin nhắn" autocomplete="off" enterkeyhint="enter"
            .value=${liveValue(t.draft)} @input=${onDraft} @keydown=${onKey}></textarea>
          <button class="send" type="submit" aria-label="Gửi">${icons.send()}</button>
        </form>
      </section>`;
  }

  const view = () => html`
    <div class="chat" data-pane=${openId === null ? 'list' : 'thread'}>
      <section class="list-pane">
        ${appBar({ title: 'Gom', actions: html`${themeButton()}
          <a class="ib" href="/friends" aria-label="Bạn bè">${icons.users()}</a>
          <a class="ib" href="/settings" aria-label="Cài đặt">${icons.gear()}</a>` })}
        ${banner()}
        ${errorBox(error)}
        ${notice === '' ? nothing : html`<p class="note" role="status">${notice}</p>`}
        ${me === null || chats === null ? nothing : chats.length === 0 ? empty() : html`<ul class="items">${repeat(chats, (c) => c.id, row)}</ul>`}
      </section>
      ${threadPane()}
    </div>`;

  function update(): void {
    render(view(), root);
    const after = root.querySelector<HTMLElement>('.msgs');
    if (after === null || openId === null) return;
    const t = threadOf(openId);
    if (scrollAnchor !== null) { after.scrollTop += after.scrollHeight - scrollAnchor; scrollAnchor = null; }
    else if (t.stick) after.scrollTop = after.scrollHeight;
  }

  // ---- the person's actions

  function onRowClick(e: MouseEvent, id: string): void {
    if (e.defaultPrevented || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
    e.preventDefault();
    open(id);
  }

  function onBack(e: MouseEvent): void {
    if (e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
    e.preventDefault();
    deps.nav.push('/');
    openId = null;
    update();
  }

  function onScroll(id: string, box: HTMLElement): void {
    threadOf(id).stick = box.scrollHeight - box.scrollTop - box.clientHeight < GAP_NEAR_BOTTOM_PX;
  }

  function onDraft(e: Event): void {
    const field = e.target as HTMLTextAreaElement;
    if (openId !== null) threadOf(openId).draft = field.value;
    field.style.height = 'auto';
    field.style.height = `${Math.min(field.scrollHeight, 140)}px`;
  }

  function onKey(e: KeyboardEvent): void {
    // Enter sends on a keyboard; Shift+Enter is a new line; a word still being composed (Telex, VNI) must not be cut off.
    if (e.key !== 'Enter' || e.shiftKey || e.isComposing || deps.coarsePointer() || openId === null) return;
    e.preventDefault();
    void send(openId);
  }

  function open(id: string): void {
    deps.nav.push(`/c/${id}`);
    show(id);
  }

  /** Makes this conversation the open one: starts loading it, and remembers what was unread now. */
  function show(id: string): void {
    openId = id;
    const t = threadOf(id);
    const c = chatOf(id);
    if (c !== undefined) { t.lastReadAtOpen = c.lastReadSeq; t.unreadAtOpen = c.unread; }
    t.stick = true;
    update();
    if (!t.loaded) void loadHistory(id);
    else markRead(id);
  }

  // ---- loading

  async function loadHistory(id: string): Promise<void> {
    const t = threadOf(id);
    if (t.loading) return;
    t.loading = true;
    const result = await api.history(id, undefined);
    t.loading = false;
    if (!result.ok) { handleFailure(result); return; }
    t.messages = mergeMessages(t.messages, result.data.messages);
    t.hasMore = result.data.hasMore;
    t.loaded = true;
    update();
    markRead(id);
  }

  async function loadOlder(id: string): Promise<void> {
    const t = threadOf(id);
    const oldest = t.messages[0]?.seq;
    if (t.loading || oldest === undefined) return;
    t.loading = true; update();
    const result = await api.history(id, oldest);
    t.loading = false;
    if (!result.ok) { handleFailure(result); return; }
    t.messages = mergeMessages(result.data.messages, t.messages);
    t.hasMore = result.data.hasMore;
    t.stick = false;
    scrollAnchor = root.querySelector<HTMLElement>('.msgs')?.scrollHeight ?? null;
    update();
  }

  async function refreshList(): Promise<void> {
    const result = await api.conversations();
    if (!result.ok) { handleFailure(result); return; }
    chats = result.data;
    error = null;
    update();
  }

  function handleFailure(failure: Failure): void {
    if (failure.error === 'unauthorized') { go('/sign-in'); return; }
    error = failure;
    update();
  }

  // ---- reading and the list

  function markRead(id: string): void {
    const c = chatOf(id);
    const t = threads.get(id);
    if (c === undefined || t === undefined || !t.loaded || !deps.visible()) return;
    const newest = newestSeq(t);
    if (newest <= c.lastReadSeq && c.unread === 0) return;
    c.lastReadSeq = Math.max(c.lastReadSeq, newest);
    c.unread = 0;
    update();
    if (newest > 0) void api.markRead(id, newest);
  }

  /** Takes a created event into the thread and the list. Safe to apply twice: the second time changes nothing. */
  function apply(conversation: string, event: ChatEvent): void {
    const message = event.message;
    const c = chatOf(conversation);
    if (c === undefined) { if (chats !== null) void refreshList(); return; }

    const t = threads.get(conversation);
    if (t !== undefined) {
      const had = newestSeq(t);
      t.pending = t.pending.filter((p) => p.clientId !== message.clientId);
      t.messages = mergeMessages(t.messages, [message]);
      // A number is missing: ask the server to fill in from the last one we held.
      if (t.loaded && event.seq > had + 1) live?.sync(conversation, had);
    }

    if (event.seq > c.lastSeq) {
      c.lastSeq = event.seq;
      c.lastMessage = { seq: message.seq, body: message.body.length > PREVIEW_CHARS ? message.body.slice(0, PREVIEW_CHARS) : message.body, createdAt: message.createdAt, sender: message.sender };
      if (message.sender.gomId !== me?.gomId && !(conversation === openId && deps.visible())) c.unread++;
      chats = [c, ...(chats ?? []).filter((x) => x !== c)];
    }
    update();
    if (conversation === openId) markRead(conversation);
  }

  // ---- writing

  /** The whole outbox, written down so a reload or a discarded tab does not lose what has not gone yet. */
  function persist(): void {
    if (outbox === null) return;
    outbox.save([...threads].flatMap(([id, t]) => t.pending.filter((p) => p.state !== 'failed')
      .map((p) => ({ conversation: id, clientId: p.clientId, body: p.body, createdAt: p.createdAt }))));
  }

  async function send(id: string): Promise<void> {
    const t = threadOf(id);
    const body = t.draft.trim();
    if (body === '' || me === null) return;
    t.draft = '';
    const clientId = deps.newId();
    // If an earlier message is still waiting, this one waits behind it: they must reach the server in the order written.
    const waiting = t.pending.some((x) => x.state === 'queued');
    t.pending.push({
      id: clientId, clientId, seq: 0, conversationId: id, sender: { gomId: me.gomId, displayName: me.displayName }, body,
      createdAt: deps.now().toISOString(), state: waiting ? 'queued' : 'sending',
    });
    t.stick = true;
    persist();
    update();
    if (!waiting) await flush(id);
  }

  /** Sends the waiting messages of one conversation, one at a time, until they are gone, or the network or server says wait. */
  async function flush(id: string): Promise<void> {
    const t = threadOf(id);
    if (t.flushing) return;
    t.flushing = true;
    try {
      for (;;) {
        const p = t.pending.find((x) => x.state !== 'failed');
        if (p === undefined) return;
        p.state = 'sending';
        delete p.error;
        update();
        const result = await api.send(id, p.clientId, p.body);
        if (result.ok) {
          t.failures = 0;
          t.attempts.delete(p.clientId);
          apply(id, { seq: result.data.seq, kind: 'created', message: result.data });
          persist();
          continue;
        }
        if (result.error === 'unauthorized') { p.state = 'queued'; update(); go('/sign-in'); return; }

        const transient = result.error === 'network' || result.error === 'server' || result.error === 'too_many_messages';
        const tries = (t.attempts.get(p.clientId) ?? 0) + (result.error === 'network' ? 0 : 1);
        t.attempts.set(p.clientId, tries);
        if (transient && tries < MAX_SERVER_TRIES) {
          p.state = 'queued';
          p.error = result;
          persist();
          update();
          scheduleRetry(id, result);
          return;
        }
        // Turned away for good: it stays, with the reason, and the ones behind it go on.
        p.state = 'failed';
        p.error = result;
        t.attempts.delete(p.clientId);
        persist();
        update();
      }
    } finally {
      t.flushing = false;
    }
  }

  function scheduleRetry(id: string, failure: Failure): void {
    const t = threadOf(id);
    if (t.retryTimer !== null) deps.timer.clear(t.retryTimer);
    const wait = failure.error === 'too_many_messages' && failure.retryAfter !== undefined
      ? Math.max(1, failure.retryAfter) * 1000
      : Math.min(MAX_RETRY_WAIT_MS, 1000 * 2 ** t.failures);
    t.failures++;
    t.retryTimer = deps.timer.set(() => { t.retryTimer = null; void flush(id); }, wait);
  }

  /** The connection is back (or the page is shown again): try everything that is waiting now rather than at the next tick of the timer. */
  function retryAll(): void {
    for (const [id, t] of threads) {
      if (!t.pending.some((p) => p.state === 'queued')) continue;
      if (t.retryTimer !== null) { deps.timer.clear(t.retryTimer); t.retryTimer = null; }
      void flush(id);
    }
  }

  function retry(id: string, clientId: string): void {
    const t = threadOf(id);
    const p = t.pending.find((x) => x.clientId === clientId);
    if (p === undefined) return;
    p.state = 'queued';
    delete p.error;
    persist();
    void flush(id);
  }

  function discard(id: string, clientId: string): void {
    const t = threadOf(id);
    t.pending = t.pending.filter((x) => x.clientId !== clientId);
    update();
  }

  /** Puts back what was left in the outbox when the page last closed, and sends it. The same ids make a second send harmless. */
  function restoreOutbox(): void {
    if (outbox === null || me === null) return;
    for (const item of outbox.load()) {
      if (chatOf(item.conversation) === undefined) continue;   // no longer in that conversation: let it go
      threadOf(item.conversation).pending.push({
        id: item.clientId, clientId: item.clientId, seq: 0, conversationId: item.conversation,
        sender: { gomId: me.gomId, displayName: me.displayName }, body: item.body, createdAt: item.createdAt, state: 'queued',
      });
    }
    persist();
    update();
    for (const [id, t] of threads) if (t.pending.length > 0) void flush(id);
  }

  // ---- start

  const handlers: LiveHandlers = {
    state(connected) {
      offline = !connected;
      update();
      if (!connected) return;
      retryAll();
      void refreshList().then(() => {
        if (openId === null) return;
        const t = threads.get(openId);
        if (t?.loaded) live?.sync(openId, newestSeq(t));
      });
    },
    event: (conversation, event) => apply(conversation, event),
    events(conversation, events, hasMore) {
      for (const e of events) apply(conversation, e);
      const last = events[events.length - 1];
      if (hasMore && last !== undefined) live?.sync(conversation, last.seq);
    },
    ended: () => go('/sign-in'),
    replaced() { notice = 'Gom đang mở ở nơi khác nên kết nối ở đây đã ngắt. Hãy tải lại trang để kết nối lại.'; update(); },
  };

  deps.nav.onPop(() => { const id = idFromPath(); if (id === null) { openId = null; update(); } else show(id); });
  document.addEventListener('visibilitychange', () => {
    if (!deps.visible()) return;
    live?.retryNow();
    retryAll();
    if (openId !== null) markRead(openId);
  });

  addEventListener('online', () => { live?.retryNow(); retryAll(); });

  update();
  void api.me().then(async (result) => {
    if (!result.ok) { handleFailure(result); return; }
    me = result.data;
    outbox = deps.outbox(me.gomId);
    live = deps.live(handlers);
    live.start();
    await refreshList();
    restoreOutbox();
    if (openId !== null) show(openId);
  });
}
