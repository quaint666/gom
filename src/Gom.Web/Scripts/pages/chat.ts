import { html, nothing, render, type TemplateResult } from 'lit-html';
import { live as liveValue } from 'lit-html/directives/live.js';
import { repeat } from 'lit-html/directives/repeat.js';
import { html as staticHtml, unsafeStatic } from 'lit-html/static.js';
import type { Api, Attachment, ChatEvent, ChatMessage, Conversation, Failure, GroupMember, Reply, User } from '../api.js';
import { linkify } from '../linkify.js';
import { avatarTone, buildThread, clock, initials, listTime, mergeMessages, type Shown, type ThreadItem } from '../chat/model.js';
import type { LiveControl, LiveHandlers } from '../chat/live.js';
import * as icons from '../icons.js';
import { errorBox } from '../ui.js';
import { matches } from '../search.js';
import { errorText, formatSize } from '../text.js';

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

export interface OutboxItem { conversation: string; clientId: string; body: string; createdAt: string; replyTo?: Reply; attachments?: Attachment[] }

/** How many pictures and files one message may carry (the server's limit is the same by default). */
const MAX_ATTACHMENTS = 10;
/** How many uploads go up at the same time. */
const UPLOADS_AT_ONCE = 2;

/** One picture or file chosen for the next message: it goes up at once, and joins the message when that is sent. */
interface TrayItem { key: number; name: string; file: File | null; state: 'waiting' | 'uploading' | 'done' | 'failed'; progress: number; attachment?: Attachment; error?: Failure }

/** The mark on your own message: one check when sent, two when seen, none otherwise. */
type Tick = 'sent' | 'seen' | null;

interface Thread {
  messages: ChatMessage[];
  pending: Shown[];
  hasMore: boolean;
  loading: boolean;
  loaded: boolean;
  /** The last step of the stream this device knows of. It runs ahead of the newest message when a message was taken back. */
  cursor: number;
  /** How far each other member has read, if you and they both share it. */
  reads: { enabled: boolean; seqs: Map<string, number> };
  /** What you had read, and how many were new, when you opened it: the "new messages" divider. */
  lastReadAtOpen: number;
  unreadAtOpen: number;
  /** Opened with unread messages: the next time the divider is drawn, scroll to it instead of to the bottom. */
  jump: boolean;
  /** Messages from others that arrived while you were reading further up. */
  below: number;
  /** The messages pinned to the top, newest pin first. */
  pins: ChatMessage[];
  /** Whether the list of pins (more than the newest) is open. */
  pinsOpen: boolean;
  /** The message of yours whose words are being changed in the composer. */
  editing: { id: string } | null;
  draft: string;
  /** The pictures and files chosen for the next message. */
  tray: TrayItem[];
  /** The message the next one will answer, shown above the composer. */
  replying: Reply | null;
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
/** The reactions the server accepts, in the order they are offered. */
const EMOJI = ['👍', '❤️', '😆', '😮', '😢', '😡'] as const;
/** How much of the answered message the bar above the composer shows (the server keeps the same number for the quote). */
const QUOTE_CHARS = 100;
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
  /** `at` is where it was asked for (the pointer or the button), so a mouse gets it there; a finger always gets the sheet at the bottom. */
  let menu: { id: string; confirming: boolean; hide?: boolean; at?: { x: number; y: number } } | null = null;
  /** The search bar of the open conversation: what was asked, and what came back (null while waiting for the first answer). */
  /** Messages that just arrived from someone else in the open chat: they rise in once, and are forgotten a moment later. */
  const fresh = new Set<string>();
  /** The gear menu in the side rail, and the info panel beside a thread (its open or closed state is remembered on this device). */
  let gearOpen = false;
  /** The picture shown large: which message, and which of its pictures. */
  let viewer: { messageId: string; index: number } | null = null;
  let trayKey = 0;
  let infoOpen = ((): boolean => { try { return localStorage.getItem('gom-info') === '1'; } catch { return false; } })();
  const people = new Map<string, GroupMember[]>();
  let searching: { q: string; results: ChatMessage[] | null; asked: string; ticket: number } | null = null;
  let found: string | null = null;
  /** What is typed in the search above the list of conversations. */
  let filter = '';
  /** The message being passed on, while the person picks where to. */
  let forwarding: Shown | null = null;

  function idFromPath(): string | null { return CONVERSATION_PATH.exec(deps.nav.path())?.[1]?.toLowerCase() ?? null; }
  const chatOf = (id: string | null): Conversation | undefined => (id === null ? undefined : chats?.find((c) => c.id === id));
  const newestSeq = (t: Thread): number => t.messages[t.messages.length - 1]?.seq ?? 0;

  function threadOf(id: string): Thread {
    let t = threads.get(id);
    if (t === undefined) {
      t = { messages: [], pending: [], hasMore: false, loading: false, loaded: false, lastReadAtOpen: 0, unreadAtOpen: 0, jump: false, below: 0, pins: [], pinsOpen: false, editing: null, draft: '', tray: [], replying: null, stick: true, cursor: 0, reads: { enabled: false, seqs: new Map() }, flushing: false, retryTimer: null, failures: 0, attempts: new Map() };
      threads.set(id, t);
    }
    return t;
  }

  // ---- drawing

  const avatar = (name: string): TemplateResult => html`<div class="av a${avatarTone(name)}" aria-hidden="true">${initials(name)}</div>`;

  function preview(c: Conversation): string {
    const last = c.lastMessage;
    if (last === null) return 'Chưa có tin nhắn';
    if (last.deleted) return recalledText(last.sender.id === me?.id, c.kind === 'group' ? last.sender.displayName : null);
    const who = last.sender.id === me?.id ? 'Bạn: ' : c.kind === 'group' ? `${last.sender.displayName}: ` : '';
    return who + (last.body !== '' ? last.body : last.attachment === 'image' ? 'Đã gửi một hình ảnh' : last.attachment === 'file' ? 'Đã gửi một tệp' : '');
  }

  const row = (c: Conversation) => html`
    <li><a class="item ${c.unread > 0 ? 'unread' : ''} ${c.id === openId ? 'active' : ''}" href="/c/${c.id}" data-chat=${c.id}
      aria-current=${c.id === openId ? 'page' : nothing} @click=${(e: MouseEvent) => onRowClick(e, c.id)}>
      ${avatar(c.title ?? '?')}
      <div class="im">
        <div class="l1"><span class="nm">${c.title ?? '…'}</span>${c.lastMessage ? html`<span class="tm">${listTime(c.lastMessage.createdAt, deps.now())}</span>` : nothing}</div>
        <div class="l2"><span class="pv">${preview(c)}</span>
          ${c.muted ? html`<span class="mute-mark" role="img" aria-label="Đã tắt thông báo">${icons.bellOff()}</span>` : nothing}
          ${c.unread > 0 ? html`<span class="badge ${c.muted ? 'muted' : ''}" aria-label="${c.unread} tin chưa đọc">${c.unread > 99 ? '99+' : c.unread}</span>` : nothing}</div>
      </div></a></li>`;

  /** The conversations whose name matches what is typed above the list (all of them when nothing is typed). */
  const visibleChats = (): Conversation[] => (chats ?? []).filter((c) => matches(c.title ?? '', filter));

  const banner = () => offline ? html`<div class="warn" role="status">${icons.wifiOff()}<span>Mất kết nối. Đang kết nối lại…</span></div>` : nothing;

  const empty = () => html`
    <div class="empty">
      <div class="big-icon">${icons.logo()}</div>
      <h2>Chưa có cuộc trò chuyện nào</h2>
      <p class="lead">Hãy kết bạn rồi bắt đầu trò chuyện.</p>
      <a class="btn" href="/friends">Thêm bạn bè</a>
    </div>`;

  /** What stands where a message was: in your voice, or theirs (with their name in a group). */
  const recalledText = (mine: boolean, name: string | null): string =>
    mine ? 'Bạn đã thu hồi một tin nhắn' : name === null ? 'Tin nhắn đã được thu hồi' : `${name} đã thu hồi một tin nhắn`;

  /** One line of the menu: an icon and a word. */
  const menuItem = (attr: string, icon: TemplateResult, label: string, run: () => void, danger = false): TemplateResult =>
    staticHtml`<button class="mi ${danger ? 'danger' : ''}" type="button" role="menuitem" data-${unsafeStatic(attr)} @click=${run}>${icon}<span>${label}</span></button>`;

  /**
   * The menu of the open message: a small list beside the pointer for a mouse, a sheet at the bottom for a finger (the
   * stylesheet decides which). It floats over the thread, so opening it never moves the messages.
   */
  const menuOverlay = (t: Thread) => {
    if (menu === null) return nothing;
    const m = [...t.messages, ...t.pending].find((x) => x.id === menu!.id);
    if (m === undefined) return nothing;
    const mine = m.sender.id === me?.id;
    const close = () => { menu = null; update(); };
    const pinned = t.pins.some((p) => p.id === m.id);
    return html`
      <div class="scrim" data-scrim @click=${close}></div>
      <div class="menu ${mine ? 'mine' : ''}" role="menu" aria-label="Tùy chọn tin nhắn" @keydown=${(e: KeyboardEvent) => { if (e.key === 'Escape') close(); }}>
      ${menu.hide ? html`
        <p>Xóa tin nhắn này chỉ ở phía bạn? Người khác vẫn thấy nó.</p>
        <div class="ask"><button class="btn small" type="button" data-confirm-hide @click=${() => void hide(m)}>Xóa với tôi</button>
        <button class="btn secondary small" type="button" data-cancel-hide @click=${close}>Hủy</button></div>` : menu.confirming ? html`
        <p>Thu hồi tin nhắn này với mọi người?</p>
        <div class="ask"><button class="btn small" type="button" data-confirm-recall @click=${() => void recall(m)}>Thu hồi</button>
        <button class="btn secondary small" type="button" data-cancel-recall @click=${close}>Hủy</button></div>` : html`
        <div class="emoji-row" role="group" aria-label="Thả cảm xúc">${EMOJI.map((e) => html`
          <button class="emoji" type="button" data-react=${e} aria-label=${`Thả ${e}`} @click=${() => void setReaction(m, e)}>${e}</button>`)}</div>
        <div class="items-grid">
        ${menuItem('reply', icons.reply(), 'Trả lời', () => startReply(m))}
        ${menuItem('copy', icons.copy(), 'Sao chép', () => void copyText(m))}
        ${pinned ? menuItem('unpin', icons.pin(), 'Bỏ ghim', () => void setPin(m, false)) : menuItem('pin', icons.pin(), 'Ghim', () => void setPin(m, true))}
        ${menuItem('forward', icons.forward(), 'Chuyển tiếp', () => { menu = null; forwarding = m; update(); })}
        ${mine ? menuItem('edit', icons.pencil(), 'Chỉnh sửa', () => startEdit(m)) : nothing}
        ${menuItem('hide', icons.trash(), 'Xóa với tôi', () => { menu = { id: m.id, confirming: false, hide: true }; update(); })}
        ${mine ? menuItem('recall', icons.undo(), 'Thu hồi', () => { menu = { id: m.id, confirming: true }; update(); }, true) : nothing}
        </div>
        <button class="btn secondary small close-menu" type="button" data-cancel-recall @click=${close}>Đóng</button>`}
      </div>`;
  };

  /** The reactions under a message: each emoji with how many chose it; yours is marked, and tapping a chip is choosing it or taking yours back. */
  const chips = (m: Shown) => m.deleted || !m.reactions || m.reactions.length === 0 ? nothing : html`
    <div class="reacts ${m.sender.id === me?.id ? 'out' : 'in'}">${m.reactions.map((r) => {
      const mine = r.users.some((u) => u.id === me?.id);
      return html`<button class="chip ${mine ? 'mine' : ''}" type="button" data-chip=${r.emoji} aria-pressed=${mine ? 'true' : 'false'}
        aria-label=${`${r.emoji}: ${r.users.map((u) => u.id === me?.id ? 'Bạn' : u.displayName).join(', ')}`}
        @click=${() => void setReaction(m, mine ? null : r.emoji)}>${r.emoji}<span>${r.users.length}</span></button>`;
    })}</div>`;

  /** What stands above an answer: who wrote the message it answers and the start of it, or that it is no longer there to see. */
  const quote = (r: Reply) => html`
    <div class="quote" data-quote=${r.id}>${r.sender === null || r.snippet === null
      ? html`<span class="gone">Tin nhắn không còn hiển thị</span>`
      : html`<b>${r.sender.id === me?.id ? 'Bạn' : r.sender.displayName}</b><span>${r.snippet}</span>`}</div>`;

  /** The words of a message, with every http or https address made into a link that opens in a new tab and tells that site nothing about this one. */
  const words = (body: string) => linkify(body).map((p) => 'url' in p
    ? html`<a class="ext" href=${p.url} target="_blank" rel="noopener noreferrer nofollow">${p.text}</a>` : p.text);

  /** The pictures of a message as a grid, then its files as cards. */
  const attachmentsOf = (m: ChatMessage): TemplateResult | typeof nothing => {
    const all = m.attachments ?? [];
    if (all.length === 0) return nothing;
    const pictures = all.filter((a) => a.kind === 'image');
    const files = all.filter((a) => a.kind === 'file');
    return html`
      ${pictures.length === 0 ? nothing : html`<div class=${`pics n${Math.min(pictures.length, 5)}`} data-pics>${pictures.map((a, i) => html`
        <button class="pic" type="button" data-pic=${a.id} aria-label=${`Xem ảnh ${i + 1} trên ${pictures.length}`} @click=${() => { viewer = { messageId: m.id, index: i }; update(); root.querySelector<HTMLElement>('.viewer')?.focus(); }}>
          <img src=${`/api/attachments/${a.id}/thumb`} alt=${a.name} loading="lazy" decoding="async" width=${a.width ?? nothing} height=${a.height ?? nothing}></button>`)}</div>`}
      ${files.map((a) => html`
        <a class="filecard" href=${`/api/attachments/${a.id}`} download=${a.name} data-file=${a.id}>${icons.file()}
          <span class="fi"><b>${a.name}</b><small>${formatSize(a.size)}</small></span>${icons.download()}</a>`)}`;
  };

  /** The big picture, with the others of its message one step away. Escape or a click outside closes it. */
  const viewerView = () => {
    if (viewer === null || openId === null) return nothing;
    const message = threadOf(openId).messages.find((x) => x.id === viewer!.messageId) ?? threadOf(openId).pending.find((x) => x.id === viewer!.messageId);
    const pictures = (message?.attachments ?? []).filter((a) => a.kind === 'image');
    const current = pictures[viewer.index];
    if (current === undefined) return nothing;
    const step = (by: number) => { viewer = { messageId: viewer!.messageId, index: (viewer!.index + by + pictures.length) % pictures.length }; update(); };
    return html`
      <div class="viewer" role="dialog" aria-modal="true" aria-label="Xem ảnh" tabindex="-1" data-viewer
        @keydown=${(e: KeyboardEvent) => { if (e.key === 'Escape') { viewer = null; update(); } else if (e.key === 'ArrowLeft' && pictures.length > 1) step(-1); else if (e.key === 'ArrowRight' && pictures.length > 1) step(1); }}
        @click=${(e: MouseEvent) => { if (e.target === e.currentTarget) { viewer = null; update(); } }}>
        <div class="viewer-bar">
          <span>${viewer.index + 1} / ${pictures.length}</span>
          <a class="ib" href=${`/api/attachments/${current.id}`} download=${current.name} aria-label="Tải ảnh xuống" data-viewer-download>${icons.download()}</a>
          <button class="ib" type="button" data-viewer-close aria-label="Đóng" @click=${() => { viewer = null; update(); }}>${icons.close()}</button>
        </div>
        ${pictures.length > 1 ? html`<button class="ib vprev" type="button" data-viewer-prev aria-label="Ảnh trước" @click=${() => step(-1)}>${icons.back()}</button>
          <button class="ib vnext" type="button" data-viewer-next aria-label="Ảnh sau" @click=${() => step(1)}>${icons.next()}</button>` : nothing}
        <img class="vimg" src=${`/api/attachments/${current.id}`} alt=${current.name}>
      </div>`;
  };

  /** What is chosen for the next message, above the box where it is written. */
  const trayView = (t: Thread) => t.tray.length === 0 ? nothing : html`
    <div class="atray" role="group" aria-label="Tệp đính kèm" data-tray>
      ${repeat(t.tray, (i) => i.key, (i) => html`
        <div class=${`tray-item ${i.state}`} data-tray-item=${i.key}>
          ${i.state === 'done' && i.attachment?.kind === 'image' ? html`<img src=${`/api/attachments/${i.attachment.id}/thumb`} alt=${i.name}>`
            : html`<div class="tray-file">${icons.file()}<span>${i.name}</span></div>`}
          ${i.state === 'uploading' || i.state === 'waiting' ? html`<progress max="1" value=${i.state === 'waiting' ? 0 : i.progress} aria-label=${`Đang tải lên ${i.name}`}></progress>` : nothing}
          ${i.state === 'failed' ? html`<div class="tray-err" role="alert">${i.error ? errorText(i.error) : 'Không tải lên được.'}
            <button class="btn secondary small" type="button" data-tray-retry @click=${() => retryTray(i)}>Thử lại</button></div>` : nothing}
          <button class="ib tray-x" type="button" data-tray-remove aria-label=${`Gỡ ${i.name}`} @click=${() => { t.tray = t.tray.filter((x) => x !== i); update(); }}>${icons.close()}</button>
        </div>`)}
    </div>`;

  const bubble = (item: Extract<ThreadItem, { kind: 'msg' }>, group: boolean, seen: string | null, tick: Tick): TemplateResult => {
    const m = item.msg;
    const others = group && !item.mine;
    const classes = `mrow ${item.mine ? 'out' : 'in'} ${item.first ? 'first' : ''} ${item.last ? 'last' : ''} ${m.deleted ? 'recalled' : ''} ${found === m.id ? 'found' : ''} ${fresh.has(m.id) ? 'fresh' : ''}`;
    // A message that has arrived has a menu (answer, copy, and for your own, take back): not one still on its way, nor one taken back.
    const hasMenu = m.state === undefined && m.deleted !== true;
    const tools = hasMenu ? html`
      <div class="tools" role="group" aria-label="Tùy chọn tin nhắn">
        <button class="ib small" type="button" data-quick-reply aria-label="Trả lời" @click=${() => startReply(m)}>${icons.reply()}</button>
        <button class="ib small menu-btn" type="button" data-menu aria-label="Tùy chọn tin nhắn" aria-haspopup="menu" aria-expanded=${menu?.id === m.id ? 'true' : 'false'}
          @click=${(e: MouseEvent) => { menu = menu?.id === m.id ? null : { id: m.id, confirming: false, at: pointAt(e) }; update(); }}>${icons.more()}</button>
      </div>` : nothing;
    // The time shows on the last message of a run; the others show it when held over (title) and to a screen reader.
    const sending = m.state === 'sending' || m.state === 'queued';
    const showTime = item.last || sending || m.editedAt !== undefined && m.editedAt !== null && !m.deleted;
    return html`
      <div class=${classes} data-msg=${m.id} data-state=${m.state ?? 'sent'}>
        ${item.mine ? tools : nothing}
        ${others ? (item.last ? html`<div class="av sm a${avatarTone(m.sender.displayName)}" aria-hidden="true">${initials(m.sender.displayName)}</div>` : html`<div class="sp"></div>`) : nothing}
        <div class="bub" title=${clock(m.createdAt)} @contextmenu=${(e: MouseEvent) => onContext(e, m, hasMenu)} @pointerdown=${(e: PointerEvent) => pressStart(e, m, hasMenu)}
          @pointermove=${pressMove} @pointerup=${pressEnd} @pointercancel=${pressEnd} @pointerleave=${pressEnd}>${others && item.first ? html`<div class="sender">${m.sender.displayName}</div>` : nothing}${m.deleted ? html`<span class="txt gone">${recalledText(item.mine, others ? m.sender.displayName : null)}</span>` : html`${m.reply ? quote(m.reply) : nothing}${attachmentsOf(m)}${m.body === '' ? nothing : html`<span class="txt">${words(m.body)}</span>`}`}
          ${showTime ? html`<span class="ts">${m.editedAt && !m.deleted ? html`<span class="edited">đã chỉnh sửa · </span>` : nothing}${sending ? html`<span class="sending" role="img" aria-label=${m.state === 'sending' ? 'Đang gửi' : 'Chờ gửi'}>${icons.clock()}</span>` : clock(m.createdAt)}${tick === null ? nothing : html` <span class=${`tick t-${tick}`} role="img" aria-label=${tick === 'seen' ? 'Đã xem' : 'Đã gửi'}>${tick === 'seen' ? icons.checks() : icons.check()}</span>`}</span>` : html`<span class="sr-only">${clock(m.createdAt)}</span>`}</div>
        ${!item.mine ? tools : nothing}
      </div>
      ${chips(m)}
      ${seen === null ? nothing : html`<div class="seen">${icons.checks()}<span>${seen}</span></div>`}
      ${m.state === 'queued' ? html`<div class="queued-note">${m.error?.error === 'network' || m.error === undefined ? 'Sẽ gửi khi có mạng' : 'Sẽ thử gửi lại'}</div>` : nothing}
      ${m.state === 'failed' ? html`
        <div class="failed" role="alert">${icons.alert()}<span>${m.error ? errorText(m.error) : 'Chưa gửi được.'}</span>
          <button class="btn secondary small" type="button" data-retry @click=${() => retry(m.conversationId, m.clientId)}>Gửi lại</button>
          <button class="btn secondary small" type="button" data-discard @click=${() => discard(m.conversationId, m.clientId)}>Xóa</button></div>` : nothing}`;
  };

  const threadItem = (item: ThreadItem, group: boolean, seenText: (msg: Shown) => string | null, tick: (msg: Shown) => Tick) => {
    if (item.kind === 'day') return html`<div class="day">${item.label}</div>`;
    if (item.kind === 'new') return html`<div class="newdiv"><span>${item.count} tin nhắn mới</span></div>`;
    return bubble(item, group, seenText(item.msg), tick(item.msg));
  };

  /** The mark on your own message: one check once it has gone out, two once it was seen (in a group, by everyone else). Nothing when you hide your reading, or for what is not yours. */
  function tickOf(t: Thread, c: Conversation | undefined): (msg: Shown) => Tick {
    const others = c?.kind === 'group' ? Math.max(1, (c.memberCount ?? 2) - 1) : 1;
    return (msg) => {
      if (msg.sender.id !== me?.id || msg.state !== undefined || msg.deleted === true) return null;
      const readers = t.reads.enabled ? [...t.reads.seqs.values()].filter((seq) => seq >= msg.seq).length : 0;
      return readers >= others ? 'seen' : 'sent';
    };
  }

  /** The "seen" line goes under your last sent message that has not been taken back, once someone has read up to it. */
  function seenText(t: Thread, group: boolean): (msg: Shown) => string | null {
    const lastMine = [...t.messages].reverse().find((m) => m.sender.id === me?.id && m.deleted !== true);
    const count = lastMine === undefined ? 0 : [...t.reads.seqs.values()].filter((seq) => seq >= lastMine.seq).length;
    return (msg) => group && lastMine !== undefined && msg.id === lastMine.id && count > 0 ? `Đã xem bởi ${count}` : null;
  }

  /** Looking for words in this conversation: a field, and the messages that contain them, newest first. */
  const searchPanel = (s: NonNullable<typeof searching>) => html`
    <div class="searchbar" role="search">
      <form data-search-form @submit=${(e: Event) => { e.preventDefault(); void runSearch(); }}>
        <div class="searchfield input">${icons.search()}
          <input type="search" name="q" aria-label="Từ cần tìm" placeholder="Tìm tin nhắn" autocomplete="off" maxlength="100" enterkeyhint="search"
            .value=${liveValue(s.q)} @input=${(e: Event) => { s.q = (e.target as HTMLInputElement).value; }}
            @keydown=${(e: KeyboardEvent) => { if (e.key === 'Escape') { searching = null; update(); } }}>
          <button class="clear" type="button" data-close-search aria-label="Đóng tìm kiếm" @click=${() => { searching = null; update(); }}>${icons.close()}</button>
        </div>
      </form>
      ${s.results === null ? nothing : s.results.length === 0
        ? html`<p class="hint" data-no-results>Không tìm thấy tin nhắn nào.</p>`
        : html`<ul class="results">${s.results.map((r) => html`
          <li><button type="button" class="result" data-result=${r.id} @click=${() => void reveal(r)}>
            <b>${r.sender.id === me?.id ? 'Bạn' : r.sender.displayName}</b><small>${listTime(r.createdAt, deps.now())}</small>
            <span>${[...r.body].length > 160 ? [...r.body].slice(0, 160).join('') + '…' : r.body}</span></button></li>`)}</ul>`}
    </div>`;
  /** The pinned messages: the newest one stays under the header, and a button opens them all. */
  const pinBar = (t: Thread) => t.pins.length === 0 ? nothing : html`
    <div class="pinbar" data-pinbar>
      <button class="pin-top" type="button" data-pin-top @click=${() => void reveal(t.pins[0]!)}>
        ${icons.pin()}<span class="pin-text"><b>Tin đã ghim</b><span>${[...t.pins[0]!.body].length > 100 ? [...t.pins[0]!.body].slice(0, 100).join('') + '…' : t.pins[0]!.body}</span></span></button>
      ${t.pins.length > 1 ? html`<button class="btn secondary small" type="button" data-pin-all aria-expanded=${t.pinsOpen ? 'true' : 'false'}
        @click=${() => { t.pinsOpen = !t.pinsOpen; update(); }}>${t.pins.length} tin</button>` : nothing}
    </div>
    ${t.pinsOpen ? html`<ul class="pinlist" data-pinlist>${t.pins.map((p) => html`
      <li><button type="button" class="result" data-pin-item=${p.id} @click=${() => void reveal(p)}>
        <b>${p.sender.id === me?.id ? 'Bạn' : p.sender.displayName}</b><small>${listTime(p.createdAt, deps.now())}</small>
        <span>${[...p.body].length > 160 ? [...p.body].slice(0, 160).join('') + '…' : p.body}</span></button></li>`)}</ul>` : nothing}`;

  /** Where to pass a message on: your conversations, newest first. The message goes as a new message from you, with nothing saying who wrote it first. */
  const forwardPanel = (m: Shown) => html`
    <div class="forward card" role="group" aria-label="Chuyển tiếp tin nhắn" data-forward-panel>
      <p><b>Chuyển tiếp đến</b></p>
      <p class="hint fw-text">${m.body.length > 120 ? [...m.body].slice(0, 120).join('') + '…' : m.body}</p>
      <ul class="fw-list">${(chats ?? []).map((c) => html`
        <li><button class="btn secondary" type="button" data-forward-to=${c.id} @click=${() => void forward(m, c.id)}>${c.title ?? '…'}</button></li>`)}</ul>
      <button class="btn secondary small" type="button" data-cancel-forward @click=${() => { forwarding = null; update(); }}>Hủy</button>
    </div>`;

  /** The narrow bar on a wide screen: messages, contacts, and a gear for settings and signing out. */
  const rail = () => html`
    <nav class="rail" aria-label="Điều hướng">
      <a class="rail-i active" href="/" aria-label="Tin nhắn" aria-current="page" @click=${onHome}>${icons.logo()}</a>
      <a class="rail-i" href="/friends" aria-label="Danh bạ">${icons.users()}</a>
      <button class="rail-i gear" type="button" data-gear aria-label="Cài đặt và tài khoản" aria-haspopup="menu" aria-expanded=${gearOpen ? 'true' : 'false'}
        @click=${() => { gearOpen = !gearOpen; update(); }}>${icons.gear()}</button>
    </nav>
    ${gearOpen ? html`
      <div class="scrim" data-gear-scrim @click=${() => { gearOpen = false; update(); }}></div>
      <div class="gearmenu" role="menu" aria-label="Tài khoản" @keydown=${(e: KeyboardEvent) => { if (e.key === 'Escape') { gearOpen = false; update(); } }}>
        <a class="mi" role="menuitem" href="/settings">${icons.gear()}<span>Cài đặt</span></a>
        ${me?.isOwner ? html`<a class="mi" role="menuitem" href="/owner">${icons.users()}<span>Lời mời và thành viên</span></a>` : nothing}
        <button class="mi" type="button" role="menuitem" data-signout @click=${() => void signOut()}><span>Đăng xuất</span></button>
      </div>` : nothing}`;

  async function signOut(): Promise<void> {
    const result = await api.signOut();
    if (result.ok || result.error === 'unauthorized') go('/sign-in'); else { gearOpen = false; handleFailure(result); }
  }

  function toggleInfo(): void {
    infoOpen = !infoOpen;
    try { localStorage.setItem('gom-info', infoOpen ? '1' : '0'); } catch { /* it is just not remembered */ }
    if (infoOpen && openId !== null) void loadPeople(openId);
    update();
  }

  async function loadPeople(id: string): Promise<void> {
    if (chatOf(id)?.kind !== 'group' || people.has(id)) return;
    const result = await api.groupMembers(id);
    if (!result.ok) return;
    people.set(id, result.data);
    update();
  }

  /** The pictures and files seen so far in this thread (what has been loaded, newest first), in the side panel. */
  const media = (t: Thread) => {
    const loaded = [...t.messages].reverse().filter((m) => m.deleted !== true);
    const pictures = loaded.flatMap((m) => (m.attachments ?? []).filter((a) => a.kind === 'image').map((a) => ({ m, a }))).slice(0, 8);
    const files = loaded.flatMap((m) => (m.attachments ?? []).filter((a) => a.kind === 'file').map((a) => ({ m, a }))).slice(0, 5);
    return html`
      ${pictures.length === 0 ? nothing : html`<h3>Ảnh</h3>
        <div class="info-media" data-info-media>${pictures.map(({ m, a }) => html`
          <button class="pic" type="button" data-info-pic=${a.id} aria-label=${a.name} @click=${() => void reveal(m)}><img src=${`/api/attachments/${a.id}/thumb`} alt="" loading="lazy"></button>`)}</div>`}
      ${files.length === 0 ? nothing : html`<h3>Tệp</h3>
        <ul class="info-list" data-info-files>${files.map(({ a }) => html`<li><a class="filecard" href=${`/api/attachments/${a.id}`} download=${a.name}>${icons.file()}<span class="fi"><b>${a.name}</b><small>${formatSize(a.size)}</small></span></a></li>`)}</ul>`}`;
  };

  /** The panel beside a thread: who it is, ways to find things in it, what is pinned, and who is in a group. */
  const infoPanel = (c: Conversation, t: Thread) => html`
    <aside class="info" aria-label="Thông tin cuộc trò chuyện" data-info-panel>
      <div class="info-top">
        <div class="av xl a${avatarTone(c.title ?? '?')}" aria-hidden="true">${initials(c.title ?? '?')}</div>
        <h2>${c.title ?? '…'}</h2>
        <small>${c.kind === 'group' ? `${c.memberCount ?? 0} thành viên` : c.peer ? `@${c.peer.gomId}` : ''}</small>
      </div>
      <div class="info-actions">
        <button class="btn secondary small" type="button" data-info-search @click=${() => { searching ??= { q: '', results: null, asked: '', ticket: 0 }; update(); root.querySelector<HTMLInputElement>('.searchbar input')?.focus(); }}>${icons.search()} Tìm tin nhắn</button>
        <button class="btn secondary small" type="button" data-info-mute @click=${() => void toggleMute(c)}>${c.muted ? icons.bellOff() : icons.bell()} ${c.muted ? 'Bật thông báo' : 'Tắt thông báo'}</button>
      </div>
      ${t.pins.length === 0 ? nothing : html`
        <h3>Tin đã ghim</h3>
        <ul class="info-list">${t.pins.map((p) => html`<li><button type="button" class="result" data-info-pin=${p.id} @click=${() => void reveal(p)}>
          <b>${p.sender.id === me?.id ? 'Bạn' : p.sender.displayName}</b><small>${listTime(p.createdAt, deps.now())}</small>
          <span>${[...p.body].length > 100 ? [...p.body].slice(0, 100).join('') + '…' : p.body}</span></button></li>`)}</ul>`}
      ${media(t)}
      ${c.kind !== 'group' ? nothing : html`
        <h3>Thành viên</h3>
        <ul class="info-list">${(people.get(c.id) ?? []).map((m) => html`<li class="info-person" data-info-member=${m.id}>
          <div class="av sm a${avatarTone(m.displayName)}" aria-hidden="true">${initials(m.displayName)}</div>
          <span>${m.displayName}${m.isMe ? ' (bạn)' : ''}</span>${m.role === 'leader' ? html`<span class="tag">Nhóm trưởng</span>` : m.role === 'deputy' ? html`<span class="tag">Phó nhóm</span>` : nothing}</li>`)}</ul>
        <a class="btn secondary small" href="/groups/${c.id}">Quản lý nhóm</a>`}
    </aside>`;

  function threadPane(): TemplateResult {
    const c = chatOf(openId);
    if (openId === null) return html`<section class="thread-pane none"><div class="empty welcome"><div class="big-icon">${icons.logo()}</div><h2>Gom</h2><p class="lead">Chọn một cuộc trò chuyện</p></div></section>`;
    if (chats !== null && c === undefined) return html`
      <section class="thread-pane">
        <header class="th-head"><a class="ib back" href="/" aria-label="Quay lại" @click=${onBack}>${icons.back()}</a></header>
        <div class="empty"><p class="lead">Không tìm thấy cuộc trò chuyện này.</p></div>
      </section>`;
    const t = threadOf(openId);
    const items = buildThread(t.messages, me?.id ?? '', t.lastReadAtOpen, t.unreadAtOpen, deps.now(), t.pending);
    return html`
      <section class="thread-pane" @dragover=${(e: DragEvent) => { if (e.dataTransfer?.types.includes('Files')) e.preventDefault(); }}
        @drop=${(e: DragEvent) => { if (e.dataTransfer?.files.length) { e.preventDefault(); addFiles(openId!, [...e.dataTransfer.files]); } }}>
        <header class="th-head">
          <a class="ib back" href="/" aria-label="Quay lại" @click=${onBack}>${icons.back()}</a>
          ${avatar(c?.title ?? '?')}
          <div class="th-title"><b>${c?.title ?? '…'}</b>${c?.peer ? html`<small>@${c.peer.gomId}</small>` : nothing}${c?.kind === 'group' ? html`<small>${c.memberCount ?? 0} thành viên</small>` : nothing}</div>
          ${c === undefined ? nothing : html`<button class="ib" type="button" data-search aria-label="Tìm trong cuộc trò chuyện" aria-expanded=${searching ? 'true' : 'false'}
            @click=${() => { searching = searching ? null : { q: '', results: null, asked: '', ticket: 0 }; update(); if (searching) root.querySelector<HTMLInputElement>('.searchbar input')?.focus(); }}>${icons.search()}</button>
          <button class="ib" type="button" data-mute aria-pressed=${c.muted ? 'true' : 'false'}
            aria-label=${c.muted ? 'Bật lại thông báo' : 'Tắt thông báo'} @click=${() => void toggleMute(c)}>${c.muted ? icons.bellOff() : icons.bell()}</button>`}
          ${c?.kind === 'group' ? html`<a class="ib" href="/groups/${c.id}" aria-label="Thông tin nhóm">${icons.users()}</a>` : nothing}
          ${c === undefined ? nothing : html`<button class="ib info-btn" type="button" data-info aria-label="Thông tin cuộc trò chuyện" aria-pressed=${infoOpen ? 'true' : 'false'} @click=${toggleInfo}>${icons.info()}</button>`}
        </header>
        ${banner()}
        ${searching === null ? nothing : searchPanel(searching)}
        ${pinBar(t)}
        <div class="msgs" @scroll=${(e: Event) => onScroll(openId!, e.target as HTMLElement)}>
          ${t.hasMore ? html`<button class="btn secondary small more" type="button" data-older ?disabled=${t.loading} @click=${() => void loadOlder(openId!)}>Tải tin cũ hơn</button>` : nothing}
          ${repeat(items, (i) => i.key, (i) => threadItem(i, c?.kind === 'group', seenText(t, c?.kind === 'group'), tickOf(t, c)))}
        </div>
        ${menuOverlay(t)}
        ${viewerView()}
        ${t.below > 0 ? html`<button class="jump" type="button" data-jump-bottom @click=${() => { t.below = 0; t.stick = true; update(); }}>${icons.down()}<span>${t.below} tin mới</span></button>` : nothing}
        ${forwarding === null ? nothing : forwardPanel(forwarding)}
        ${t.editing === null ? nothing : html`
          <div class="replybar" data-editbar>
            <div class="rb-text"><b>Đang chỉnh sửa tin nhắn</b></div>
            <button class="ib" type="button" data-cancel-edit aria-label="Hủy chỉnh sửa" @click=${() => { t.editing = null; t.draft = ''; update(); }}>${icons.close()}</button>
          </div>`}
        ${t.replying === null ? nothing : html`
          <div class="replybar" data-replybar>
            <div class="rb-text"><b>Đang trả lời ${t.replying.sender?.id === me?.id ? 'chính bạn' : t.replying.sender?.displayName ?? ''}</b><span>${t.replying.snippet ?? ''}</span></div>
            <button class="ib" type="button" data-cancel-reply aria-label="Hủy trả lời" @click=${() => { t.replying = null; update(); }}>${icons.close()}</button>
          </div>`}
        ${trayView(t)}
        <form class="composer" @submit=${(e: Event) => { e.preventDefault(); void send(openId!); }}>
          ${t.editing === null ? html`
            <button class="ib attach" type="button" data-attach aria-label="Đính kèm ảnh hoặc tệp" @click=${() => root.querySelector<HTMLInputElement>('input[name="files"]')?.click()}>${icons.attach()}</button>
            <input class="file-input" type="file" name="files" multiple tabindex="-1" aria-hidden="true"
              @change=${(e: Event) => { const input = e.target as HTMLInputElement; addFiles(openId!, [...(input.files ?? [])]); input.value = ''; }}>` : nothing}
          <textarea name="message" rows="1" aria-label="Nhập tin nhắn" placeholder="Nhập tin nhắn" autocomplete="off" enterkeyhint="enter"
            .value=${liveValue(t.draft)} @input=${onDraft} @keydown=${onKey} @paste=${(e: ClipboardEvent) => onPaste(e)}></textarea>
          <button class="send" type="submit" aria-label="Gửi" ?disabled=${t.tray.some((i) => i.state !== 'done')}>${icons.send()}</button>
        </form>
      </section>`;
  }

  const view = () => html`
    <div class="chat" data-pane=${openId === null ? 'list' : 'thread'} data-infostate=${infoOpen && openId !== null ? 'open' : 'closed'}>
      ${rail()}
      <section class="list-pane">
        <header class="bar">
          <a class="brand" href="/" aria-label="Gom, về trang chủ" @click=${onHome}>${icons.logo()}<span>Gom</span></a>
          <a class="ib" href="/friends" aria-label="Danh bạ">${icons.users()}</a>
          <a class="ib" href="/settings" aria-label="Cài đặt">${icons.gear()}</a>
        </header>
        ${chats === null || chats.length === 0 ? nothing : html`
          <div class="searchfield input list-search">${icons.search()}
            <input type="search" name="filter" aria-label="Tìm cuộc trò chuyện" placeholder="Tìm kiếm" autocomplete="off" maxlength="60"
              .value=${liveValue(filter)} @input=${(e: Event) => { filter = (e.target as HTMLInputElement).value; update(); }}
              @keydown=${(e: KeyboardEvent) => { if (e.key === 'Escape') { filter = ''; update(); } }}></div>`}
        ${banner()}
        ${errorBox(error)}
        ${notice === '' ? nothing : html`<p class="note" role="status">${notice}</p>`}
        ${me === null || chats === null ? nothing : chats.length === 0 ? empty() : visibleChats().length === 0 ? html`<p class="hint nomatch" data-no-match>Không tìm thấy cuộc trò chuyện nào.</p>` : html`<ul class="items">${repeat(visibleChats(), (c) => c.id, row)}</ul>`}
      </section>
      ${threadPane()}
      ${openId !== null && infoOpen && chatOf(openId) !== undefined ? infoPanel(chatOf(openId)!, threadOf(openId)) : nothing}
    </div>`;

  function update(): void {
    render(view(), root);
    placeMenu();
    const after = root.querySelector<HTMLElement>('.msgs');
    if (after === null || openId === null) return;
    const t = threadOf(openId);
    if (scrollAnchor !== null) { after.scrollTop += after.scrollHeight - scrollAnchor; scrollAnchor = null; return; }
    if (t.jump) {
      // The first unread message is where reading starts. It can be drawn only once the history is here.
      const divider = after.querySelector<HTMLElement>('.newdiv');
      if (divider !== null) { t.jump = false; t.stick = false; divider.scrollIntoView?.({ block: 'start' }); return; }
      if (t.loaded) t.jump = false;   // the first unread one is further back than what was loaded: read from the end
    }
    if (t.stick) after.scrollTop = after.scrollHeight;
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

  /** The Gom button: back to the start of the app (the list, nothing open). Already there, it does nothing. */
  function onHome(e: MouseEvent): void {
    if (openId === null) { if (!(e.ctrlKey || e.metaKey || e.shiftKey || e.altKey)) e.preventDefault(); return; }
    onBack(e);
  }

  function onScroll(id: string, box: HTMLElement): void {
    const t = threadOf(id);
    t.stick = box.scrollHeight - box.scrollTop - box.clientHeight < GAP_NEAR_BOTTOM_PX;
    if (t.stick && t.below > 0) { t.below = 0; update(); }
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
    t.jump = t.unreadAtOpen > 0;
    t.below = 0;
    update();
    if (!t.loaded) void loadHistory(id);
    else markRead(id);
    if (infoOpen) void loadPeople(id);
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
    t.cursor = Math.max(t.cursor, result.data.lastSeq);
    t.loaded = true;
    update();
    markRead(id);
    void fetchReads(id);
    void refreshPins(id);
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
      const had = t.cursor;
      t.pending = t.pending.filter((p) => p.clientId !== message.clientId);
      // The message being answered was taken back: there is nothing left to answer.
      if (event.kind === 'deleted' && t.replying?.id === message.id) t.replying = null;
      if (event.kind === 'deleted' && t.editing?.id === message.id) { t.editing = null; t.draft = ''; }
      if (event.kind === 'updated' && t.pins.some((p) => p.id === message.id)) void refreshPins(conversation);
      const had2 = t.messages.length;
      // A change to a message this device has not loaded is not added: a lone old message would break the paging.
      if (event.kind !== 'updated' || t.messages.some((x) => x.id === message.id)) t.messages = mergeMessages(t.messages, [message]);
      // Something new from someone else while you are reading further up: say so, and offer the way down.
      if (event.kind === 'created' && t.loaded && !t.stick && t.messages.length > had2 && message.sender.id !== me?.id && conversation === openId) t.below++;
      if (event.kind === 'created' && t.loaded && t.messages.length > had2 && message.sender.id !== me?.id && conversation === openId) {
        fresh.add(message.id);
        setTimeout(() => { fresh.delete(message.id); }, 600);
      }
      // A number is missing: ask the server to fill in from the last step we knew of.
      if (t.loaded && event.seq > had + 1) live?.sync(conversation, had);
      t.cursor = Math.max(t.cursor, event.seq);
    }

    if (event.seq > c.lastSeq) {
      c.lastSeq = event.seq;
      if (event.kind === 'deleted') {
        // A message taken back: the preview says so if it was the last one, and it is no longer something to read.
        if (c.lastMessage?.seq === message.seq) c.lastMessage = { ...c.lastMessage, body: '', deleted: true };
        if (message.seq > c.lastReadSeq && message.sender.id !== me?.id && c.unread > 0) c.unread--;
      } else if (event.kind === 'updated') {
        // The same message changed (its reactions, its words): the preview follows its words, nothing else about the list moves.
        if (c.lastMessage?.seq === message.seq) c.lastMessage = { ...c.lastMessage, body: message.body.length > PREVIEW_CHARS ? message.body.slice(0, PREVIEW_CHARS) : message.body };
      } else {
        c.lastMessage = {
          seq: message.seq, body: message.body.length > PREVIEW_CHARS ? message.body.slice(0, PREVIEW_CHARS) : message.body,
          createdAt: message.createdAt, sender: message.sender, deleted: message.deleted === true, attachment: message.attachments?.[0]?.kind ?? null,
        };
        if (message.sender.id !== me?.id && message.deleted !== true && !(conversation === openId && deps.visible())) c.unread++;
        chats = [c, ...(chats ?? []).filter((x) => x !== c)];
      }
    }
    update();
    if (conversation === openId) markRead(conversation);
  }

  // ---- who has read

  async function fetchReads(id: string): Promise<void> {
    const result = await api.reads(id);
    if (!result.ok) return;   // only a nicety: without it the line just does not show
    threadOf(id).reads = { enabled: result.data.enabled, seqs: new Map(result.data.reads.map((r) => [r.userId, r.seq])) };
    update();
  }

  // ---- muting

  /** Shows the new state at once, and puts it back with the reason if the server says no. */
  async function toggleMute(c: Conversation): Promise<void> {
    const wanted = c.muted !== true;
    c.muted = wanted;
    update();
    const result = await api.setMute(c.id, wanted);
    if (result.ok) return;
    c.muted = !wanted;
    handleFailure(result);
  }

  // ---- opening the menu without the button

  /**
   * Puts the menu where it was asked for. Set through the style object after drawing: the page's content security policy
   * refuses a style attribute (also the one a template would write on the first draw), but allows this.
   */
  function placeMenu(): void {
    const at = menu?.at;
    const pop = root.querySelector<HTMLElement>('.menu');
    if (at === undefined || pop === null) return;
    pop.style.setProperty('--x', `${Math.round(at.x)}px`);
    pop.style.setProperty('--y', `${Math.round(at.y)}px`);
  }

  /** Where a click happened; the keyboard (no pointer) gets the button's own corner. */
  function pointAt(e: MouseEvent): { x: number; y: number } {
    if (e.clientX !== 0 || e.clientY !== 0) return { x: e.clientX, y: e.clientY };
    const r = (e.currentTarget as HTMLElement | null)?.getBoundingClientRect();
    return { x: r?.left ?? 0, y: r?.bottom ?? 0 };
  }

  /** Right-click (or the context-menu key) on a message that has a menu opens it there, instead of the browser's own. */
  function onContext(e: MouseEvent, m: Shown, hasMenu: boolean): void {
    if (!hasMenu) return;
    // On an address, the browser's own menu (open, copy the address) is the one wanted.
    if (!deps.coarsePointer() && (e.target as Element | null)?.closest?.('a.ext')) return;
    e.preventDefault();
    menu = { id: m.id, confirming: false, at: { x: e.clientX, y: e.clientY } };
    update();
  }

  /** A finger held on a message for this long opens its menu. */
  const LONG_PRESS_MS = 450;
  /** The finger may drift this far (in pixels) and still count as holding still; further is a scroll. */
  const PRESS_SLOP_PX = 10;
  let press: { timer: number; x: number; y: number } | null = null;

  function pressStart(e: PointerEvent, m: Shown, hasMenu: boolean): void {
    pressEnd();
    if (!hasMenu || e.pointerType !== 'touch') return;
    const timer = deps.timer.set(() => { press = null; menu = { id: m.id, confirming: false }; update(); }, LONG_PRESS_MS);
    press = { timer, x: e.clientX, y: e.clientY };
  }

  function pressMove(e: PointerEvent): void {
    if (press !== null && Math.hypot(e.clientX - press.x, e.clientY - press.y) > PRESS_SLOP_PX) pressEnd();
  }

  function pressEnd(): void {
    if (press === null) return;
    deps.timer.clear(press.timer);
    press = null;
  }

  // ---- editing

  /** Puts the words of one of your messages in the composer; sending then changes that message instead of making a new one. */
  function startEdit(m: Shown): void {
    menu = null;
    const t = threadOf(m.conversationId);
    t.editing = { id: m.id };
    t.replying = null;
    t.draft = m.body;
    update();
    const field = root.querySelector<HTMLTextAreaElement>('.composer textarea');
    field?.focus();
  }

  async function saveEdit(id: string): Promise<void> {
    const t = threadOf(id);
    const body = t.draft.trim();
    if (t.editing === null || body === '') return;
    const target = t.editing.id;
    const result = await api.edit(id, target, body);
    if (!result.ok) { error = result; update(); return; }   // what was typed stays, so it can be tried again
    error = null;
    t.editing = null;
    t.draft = '';
    update();
  }

  // ---- reactions

  /** Chooses an emoji for a message, or (null) takes yours back. The change comes back through the stream and is drawn then. */
  async function setReaction(m: Shown, emoji: string | null): Promise<void> {
    menu = null;
    const result = emoji === null ? await api.unreact(m.conversationId, m.id) : await api.react(m.conversationId, m.id, emoji);
    if (!result.ok) { error = result; update(); return; }
    error = null;
    update();
  }

  // ---- pinned messages

  async function refreshPins(id: string): Promise<void> {
    const result = await api.pins(id);
    if (!result.ok) return;   // a nicety: without it the bar just does not show
    const t = threadOf(id);
    t.pins = result.data.pins;
    if (t.pins.length <= 1) t.pinsOpen = false;
    update();
  }

  async function setPin(m: Shown, on: boolean): Promise<void> {
    menu = null;
    const result = on ? await api.pin(m.conversationId, m.id) : await api.unpin(m.conversationId, m.id);
    if (!result.ok) { error = result; update(); return; }
    error = null;
    await refreshPins(m.conversationId);
  }

  // ---- answering and copying

  /** The next message will answer this one: shown above the composer until sent or cancelled. */
  function startReply(m: Shown): void {
    menu = null;
    const t = threadOf(m.conversationId);
    const body = [...m.body];
    t.replying = { id: m.id, sender: m.sender, snippet: body.length > QUOTE_CHARS ? body.slice(0, QUOTE_CHARS).join('') : m.body };
    update();
    root.querySelector<HTMLTextAreaElement>('.composer textarea')?.focus();
  }

  async function copyText(m: Shown): Promise<void> {
    menu = null;
    try { await navigator.clipboard.writeText(m.body); notice = 'Đã sao chép tin nhắn.'; } catch { notice = 'Không sao chép được. Hãy chọn và chép bằng tay.'; }
    update();
  }

  /**
   * Sends the words of a message to another conversation as a new message from you. It uses the same queue as writing, so a
   * lost connection does not lose it; a refusal for good (no longer friends, say) is told here, not left in a thread you are not in.
   */
  async function forward(m: Shown, target: string): Promise<void> {
    forwarding = null;
    if (me === null) return;
    const name = chatOf(target)?.title ?? '';
    const t = threadOf(target);
    const clientId = deps.newId();
    const waiting = t.pending.some((x) => x.state === 'queued');
    t.pending.push({
      id: clientId, clientId, seq: 0, conversationId: target, sender: { id: me.id, displayName: me.displayName }, body: m.body,
      createdAt: deps.now().toISOString(), state: waiting ? 'queued' : 'sending',
    });
    persist();
    update();
    if (!waiting) await flush(target);
    const p = t.pending.find((x) => x.clientId === clientId);
    if (p?.state === 'failed') {
      notice = p.error ? errorText(p.error) : 'Chưa chuyển tiếp được.';
      t.pending = t.pending.filter((x) => x.clientId !== clientId);
      persist();
    } else if (p !== undefined) notice = `Chưa gửi được đến ${name}. Sẽ gửi khi có mạng.`;
    else notice = `Đã chuyển tiếp đến ${name}.`;
    update();
  }

  // ---- searching

  async function runSearch(): Promise<void> {
    if (searching === null || openId === null) return;
    const s = searching;
    const q = s.q.trim();
    if (q === '') return;
    // Only the newest question may fill the list: an older, slower answer is dropped.
    const ticket = ++s.ticket;
    s.asked = q;
    const result = await api.search(openId, q);
    if (searching !== s || s.ticket !== ticket) return;
    if (!result.ok) { handleFailure(result); return; }
    s.results = result.data.results;
    error = null;
    update();
  }

  /** Goes to a message found by the search: older pages are loaded until it is there, then it is brought into view and marked for a moment. */
  async function reveal(m: ChatMessage): Promise<void> {
    const id = m.conversationId;
    const t = threadOf(id);
    searching = null;
    t.pinsOpen = false;
    t.stick = false;
    update();
    for (let pages = 0; !t.messages.some((x) => x.id === m.id) && t.hasMore && pages < 40; pages++) await loadOlder(id);
    found = m.id;
    update();
    root.querySelector<HTMLElement>(`[data-msg="${m.id}"]`)?.scrollIntoView?.({ block: 'center' });
    deps.timer.set(() => { if (found === m.id) { found = null; update(); } }, 2500);
  }

  // ---- deleting a message for yourself

  /** Takes a message out of this device's view: it goes from the thread, and the list is asked again (the preview may have changed). */
  function drop(conversation: string, messageId: string): void {
    const t = threads.get(conversation);
    if (t !== undefined) {
      t.messages = t.messages.filter((x) => x.id !== messageId);
      if (t.replying?.id === messageId) t.replying = null;
      if (t.editing?.id === messageId) { t.editing = null; t.draft = ''; }
    }
    if (menu?.id === messageId) menu = null;
    if (searching?.results) searching.results = searching.results.filter((r) => r.id !== messageId);
    update();
    void refreshList();
  }

  async function hide(m: Shown): Promise<void> {
    const result = await api.hideMessage(m.conversationId, m.id);
    menu = null;
    if (!result.ok) { error = result; update(); return; }
    error = null;
    drop(m.conversationId, m.id);
  }

  // ---- taking a message back

  async function recall(m: Shown): Promise<void> {
    const result = await api.recall(m.conversationId, m.id);
    menu = null;
    if (!result.ok) { error = result; update(); return; }
    error = null;
    // Show it at once. The server's own "deleted" event follows and changes nothing more.
    const t = threads.get(m.conversationId);
    if (t !== undefined) t.messages = mergeMessages(t.messages, [{ ...m, body: '', deleted: true }]);
    const c = chatOf(m.conversationId);
    if (c?.lastMessage?.seq === m.seq) c.lastMessage = { ...c.lastMessage, body: '', deleted: true };
    update();
  }

  // ---- pictures and files

  /** Puts what was chosen, pasted or dropped in the tray and starts sending it up. */
  function addFiles(id: string, files: File[]): void {
    const t = threadOf(id);
    if (t.editing !== null || files.length === 0) return;
    const room = MAX_ATTACHMENTS - t.tray.length;
    for (const file of files.slice(0, Math.max(0, room))) t.tray.push({ key: ++trayKey, name: file.name || 'tệp', file, state: 'waiting', progress: 0 });
    if (files.length > room) { notice = `Mỗi tin nhắn có tối đa ${MAX_ATTACHMENTS} tệp.`; }
    update();
    pumpTray(id);
  }

  function onPaste(e: ClipboardEvent): void {
    const files = [...(e.clipboardData?.files ?? [])];
    if (files.length === 0 || openId === null) return;   // text pastes as text
    e.preventDefault();
    addFiles(openId, files);
  }

  /** Keeps a couple of uploads going until every waiting one has gone up (or failed). */
  function pumpTray(id: string): void {
    const t = threadOf(id);
    for (;;) {
      if (t.tray.filter((i) => i.state === 'uploading').length >= UPLOADS_AT_ONCE) return;
      const next = t.tray.find((i) => i.state === 'waiting');
      if (next === undefined) return;
      void uploadOne(id, t, next);
    }
  }

  async function uploadOne(id: string, t: Thread, item: TrayItem): Promise<void> {
    item.state = 'uploading';
    item.progress = 0;
    update();
    const result = await api.upload(id, item.file!, (fraction) => { item.progress = fraction; update(); });
    if (!t.tray.includes(item)) { pumpTray(id); return; }   // taken out while it was going up
    if (result.ok) { item.state = 'done'; item.attachment = result.data; item.file = null; delete item.error; }
    else if (result.error === 'unauthorized') { go('/sign-in'); return; }
    else { item.state = 'failed'; item.error = result; }
    update();
    pumpTray(id);
  }

  function retryTray(item: TrayItem): void {
    if (item.file === null) return;
    item.state = 'waiting'; delete item.error;
    update();
    if (openId !== null) pumpTray(openId);
  }

  // ---- writing

  /** The whole outbox, written down so a reload or a discarded tab does not lose what has not gone yet. */
  function persist(): void {
    if (outbox === null) return;
    outbox.save([...threads].flatMap(([id, t]) => t.pending.filter((p) => p.state !== 'failed')
      .map((p) => ({ conversation: id, clientId: p.clientId, body: p.body, createdAt: p.createdAt, ...(p.reply ? { replyTo: p.reply } : {}), ...(p.attachments && p.attachments.length > 0 ? { attachments: p.attachments } : {}) }))));
  }

  async function send(id: string): Promise<void> {
    const t = threadOf(id);
    if (t.editing !== null) { await saveEdit(id); return; }
    const body = t.draft.trim();
    const ready = t.tray.filter((i) => i.state === 'done' && i.attachment !== undefined).map((i) => i.attachment!);
    if ((body === '' && ready.length === 0) || me === null) return;
    if (t.tray.some((i) => i.state !== 'done')) return;   // still going up, or failed: remove or retry it first
    t.draft = '';
    t.tray = [];
    const reply = t.replying;
    t.replying = null;
    const clientId = deps.newId();
    // If an earlier message is still waiting, this one waits behind it: they must reach the server in the order written.
    const waiting = t.pending.some((x) => x.state === 'queued');
    t.pending.push({
      id: clientId, clientId, seq: 0, conversationId: id, sender: { id: me.id, displayName: me.displayName }, body,
      createdAt: deps.now().toISOString(), state: waiting ? 'queued' : 'sending', ...(reply ? { reply } : {}), ...(ready.length > 0 ? { attachments: ready } : {}),
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
        const files = p.attachments?.map((a) => a.id) ?? [];
        const result = files.length > 0 ? await api.send(id, p.clientId, p.body, p.reply?.id, files) : await api.send(id, p.clientId, p.body, p.reply?.id);
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
        sender: { id: me.id, displayName: me.displayName }, body: item.body, createdAt: item.createdAt, state: 'queued', ...(item.replyTo ? { reply: item.replyTo } : {}), ...(item.attachments && item.attachments.length > 0 ? { attachments: item.attachments } : {}),
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
        if (t?.loaded) { live?.sync(openId, t.cursor); void fetchReads(openId); }
      });
    },
    event: (conversation, event) => apply(conversation, event),
    conversations() {
      void refreshList();
      if (openId !== null) void fetchReads(openId);   // a switch may have been flipped by someone
    },
    hidden: (conversation, messageId) => drop(conversation, messageId),
    pins: (conversation) => { if (threads.get(conversation)?.loaded) void refreshPins(conversation); },
    read(conversation, userId, seq) {
      const t = threads.get(conversation);
      if (t === undefined || !t.reads.enabled || seq <= (t.reads.seqs.get(userId) ?? -1)) return;
      t.reads.seqs.set(userId, seq);
      update();
    },
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
