import { html, nothing, render, type TemplateResult } from 'lit-html';
import { live as liveValue } from 'lit-html/directives/live.js';
import { repeat } from 'lit-html/directives/repeat.js';
import { NO_PUSH_SERVICE, type Pusher } from '../push.js';
import type { Api, Attachment, CustomPack, ChatEvent, ChatMessage, Conversation, Failure, GroupMember, MediaItem, Person, PersonCard, PushLevel, Reply, Result, User, CallLine } from '../api.js';
import { linkify } from '../linkify.js';
import { avatarTone, buildThread, callLength, callPreview, callTitle, clock, initials, listTime, mergeMedia, mergeMessages, missedByMe, noticeText, type Shown, type ThreadItem } from '../chat/model.js';
import type { LiveControl, LiveHandlers } from '../chat/live.js';
import * as icons from '../icons.js';
import { menuItem, menuPoint, placeMenu as placeMenuAt } from '../menu.js';
import { dismissMenu, leaveInfo, leaveThread, motionAllowed } from '../motion.js';
import { swipeReady, swiped, SWIPE_READY } from '../swipe.js';
import { avatarUrl, errorBox } from '../ui.js';
import { markedExcerpt, matches, type Run } from '../search.js';
import { clip, errorText, formatSize, tooManyAttachments } from '../text.js';
import { isAppFile } from '../appfiles.js';
import { PALETTES, PATTERNS, storedPattern } from '../theme.js';
import { limits } from '../limits.js';
import { say } from '../say.js';
import { mountWaiting } from './waiting.js';
import { STICKER_PACKS, recentStickers, rememberSticker, stickerName, stickerUrl } from '../stickers.js';
import { EMOJI_GROUPS, emojiOf, onlyEmoji, recentEmoji, rememberEmoji } from '../emoji.js';
import type { Calls } from '../calls/call.js';
import { rememberAppCalls } from '../calls/here.js';
import { audioCard, clockOf } from '../chat/audio.js';
import { VOICE_SECONDS, voiceStamp, type Recording } from '../voice.js';
import { ref } from 'lit-html/directives/ref.js';

/** Everything the page takes from its surroundings, so a test can stand in for each. */
export interface ChatDeps {
  live(handlers: LiveHandlers): LiveControl;
  nav: { path(): string; push(path: string): void; replace(path: string): void; back(): void; onPop(callback: () => void): void };
  now(): Date;
  newId(): string;
  /** A phone or tablet: Enter makes a new line there, because the send button is for sending. */
  coarsePointer(): boolean;
  visible(): boolean;
  timer: { set(fn: () => void, ms: number): number; clear(id: number): void };
  /** Messages written but not yet delivered, kept for this person across a reload. */
  outbox(gomId: string): { load(): OutboxItem[]; save(items: OutboxItem[]): void };
  /** Words typed in a conversation's message field and not sent yet, kept across a reload of this tab (not after it is closed). */
  drafts?(gomId: string): { load(conversation: string): string; save(conversation: string, text: string): void };
  /** Hands over a question the update watcher asks before reloading by itself: true while a reload would lose something. */
  holdReload?(busy: () => boolean): void;
  /** Closes this device's notifications of a conversation once it is on screen. */
  clearNotifications?(conversation: string): void;
  /** This device's notifications, for the one-time "turn them on" banner. */
  pusher?: Pusher;
  /** In the Android app: hands over each picture or GIF the keyboard puts into a text field (Gboard's GIF button). */
  keyboardImages?(callback: (file: File) => void): void;
  /**
   * Makes a picked video smaller on this device (`onProgress` from 0 to 1); null when this device cannot. Loaded only when a video
   * is picked. Without it every video goes as it is.
   */
  prepareVideo?(file: File, onProgress: (fraction: number) => void, maxBytes: number): Promise<PreparedVideo | null>;
  /** Records a voice message from the microphone (`voice.ts`); without it, or uploads off, there is no microphone button. */
  recordVoice?(): Promise<Recording | 'denied' | 'unsupported'>;
  /** Voice calls: the page's own, or the Android app's; offered only when the server has calls on and they are ready. */
  calls?: Calls;
}

/** Remembered per device (browser storage) once "Để sau" is tapped: the banner does not come back there. */
const PUSH_LATER = 'gom-push-later';

export interface OutboxItem { conversation: string; clientId: string; body: string; createdAt: string; replyTo?: Reply; attachments?: Attachment[]; sticker?: string; mentions?: string[] }

/** How long to wait before asking again after the server said "slow down". */
const SLOW_RETRY_MS = 1000;
/** A voice message being recorded in a conversation: `recorder` is null while the browser asks for the microphone; `seconds` counts up to the limit. */
interface VoiceRecording { conversation: string; recorder: Recording | null; seconds: number; timer: number | null }
/** How many uploads go up at the same time. */
const UPLOADS_AT_ONCE = 2;
/** How many steps a tray item's progress bar moves in (each step is one drawing of the page). */
const PROGRESS_STEPS = 20;
/** How many conversations one message may be passed on to at once: with a note that is twice as many sends, inside the server's burst of 20. */
const FORWARD_MAX = 5;
/** How much of a message a one-line preview shows: the pinned bar and panel, a search result or the pin list, what is being forwarded. */
const CLIP = { pin: 100, result: 160, forward: 120 } as const;
/** How long "Hoàn tác" stays after a conversation is hidden. */
const UNDO_HIDE_MS = 5000;
const COPY_NOTE_MS = 2000;
/** How long typing must pause before a search is sent, and the fewest characters that search as you type (Enter searches any). */
const SEARCH_WAIT_MS = 300;
const SEARCH_AS_YOU_TYPE_CHARS = 2;
/** The browser says when a scroll has come to rest; where it does not, a pause in scrolling stands in for it. */
const SCROLL_END = typeof window !== 'undefined' && 'onscrollend' in window;

/** One picture or file chosen for the next message: it goes up at once, and joins the message when that is sent. */
/**
 * Something chosen for the next message. A video is made smaller on this device first ('converting', with its progress), once:
 * `prepared` says that step is behind it, and `poster` is its still picture, sent after it.
 */
interface TrayItem {
  key: number; name: string; file: File | null; state: 'waiting' | 'converting' | 'uploading' | 'done' | 'failed'; progress: number; attachment?: Attachment; error?: Failure;
  prepared?: boolean; poster?: Blob | null; preview?: string;
  /** A voice message: sent on its own as soon as it is up. */
  voice?: boolean;
}

/** A video picked on this device made ready to send: an MP4 Gom plays everywhere, and its first picture. */
export interface PreparedVideo { video: Blob; poster: Blob | null }

/** Whether a picked file is a video, by its type or, when the device gives none, its name. */
export const isVideoFile = (file: File): boolean => file.type.startsWith('video/') || /\.(mp4|m4v|mov|webm|mkv|3gp)$/i.test(file.name);

/** A small picture made on this device, as an address the page may show (the page's policy allows data: pictures). */
const asDataUrl = (blob: Blob): Promise<string> => new Promise((resolve, reject) => {
  const reader = new FileReader();
  reader.onload = () => resolve(reader.result as string);
  reader.onerror = () => reject(reader.error);
  reader.readAsDataURL(blob);
});

/** "clip.mov" sent as "clip.mp4": the name says what it now is. */
const mp4Name = (name: string): string => `${(name || 'video').replace(/\.[^./]*$/, '')}.mp4`;

/** How long a video plays, as a player shows it: "0:42", "12:05", "1:02:03". Empty when unknown. */
export function playLength(ms: number | null | undefined): string {
  if (!ms || ms <= 0) return '';
  const all = Math.round(ms / 1000);
  const h = Math.floor(all / 3600), m = Math.floor((all % 3600) / 60), sec = all % 60;
  const two = (n: number) => String(n).padStart(2, '0');
  return h > 0 ? `${h}:${two(m)}:${two(sec)}` : `${m}:${two(sec)}`;
}

/** Which list of the side panel an attachment is in: videos are with the pictures, as in a phone's gallery. */
const shelf = (a: Attachment): 'image' | 'file' => a.kind === 'file' ? 'file' : 'image';

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

/** The reactions the server accepts, in the order they are offered. */
const EMOJI = ['👍', '❤️', '😆', '😮', '😢', '😡'] as const;
/** What a double tap on a message chooses. */
const HEART = EMOJI[1];
const CONVERSATION_PATH = /^\/c\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i;
const GAP_NEAR_BOTTOM_PX = 80;
/** "Is typing" stays on screen this long after the last sign of it (the page repeats the sign while its person types). */
const TYPING_SHOWN_MS = 5000;
/** While someone types, their page says so this often (the server allows one about every 2.5 s). */
const TYPING_REPEAT_MS = 3000;
/** The most names the typing line gives; the rest are counted. */
const TYPING_NAMES = 3;
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
  let recording: VoiceRecording | null = null;
  let offline = false;
  let outbox: ReturnType<ChatDeps['outbox']> | null = null;
  let drafts: ReturnType<NonNullable<ChatDeps['drafts']>> | null = null;
  let live: LiveControl | null = null;
  let scrollAnchor: number | null = null;
  /** `at` is where it was asked for (the pointer or the button), so a mouse gets it there; a finger always gets the sheet at the bottom. */
  let menu: { id: string; confirming: boolean; hide?: boolean; at?: { x: number; y: number } } | null = null;
  /** The "..." menu of one conversation in the list; `confirming` while it asks before deleting the conversation for you. */
  let rowMenu: { id: string; confirming: boolean; at?: { x: number; y: number } } | null = null;
  /** The search bar of the open conversation: what was asked, and what came back (null while waiting for the first answer). */
  /** Messages that just arrived from someone else in the open chat: they rise in once, and are forgotten a moment later. */
  const fresh = new Set<string>();
  /** The gear menu in the side rail, and the info panel beside a thread (its open or closed state is remembered on this device). */
  let gearOpen = false;
  /**
   * The picture shown large, in which conversation. `local` is the message, for a picture still being sent (the server does not list
   * it yet): then the viewer steps only through that message's pictures.
   */
  let viewer: { conversation: string; attachmentId: string; local: string | null } | null = null;
  /** After the next draw, move the viewer's strip to the current picture, at once: on opening, on a step, or when pictures came in front. */
  let viewerSync = false;
  let trayKey = 0;
  /** The banner that offers notifications: the server's key and the level to register with, while it is offered. */
  let pushOffer: { key: string; level: PushLevel; busy: boolean; failed: boolean; noService?: boolean } | null = null;
  let infoOpen = ((): boolean => { try { return localStorage.getItem('gom-info') === '1'; } catch { return false; } })();
  /** A phone: the conversation's information shown as its own page (never remembered), and its "clear" question. */
  let infoPage = false;
  let infoClearing = false;
  const people = new Map<string, GroupMember[]>();
  let searching: { q: string; results: ChatMessage[] | null; asked: string; ticket: number } | null = null;
  let found: string | null = null;
  /** Conversations whose catch-up the server turned away and that are waiting to ask again. */
  const slowWaiting = new Set<string>();
  /** Who is typing, per conversation, in the order they began (the line does not shuffle), each with the timer that ends it. */
  const typers = new Map<string, Map<string, { name: string; timer: number }>>();
  /** When this page last said that you are typing, per conversation. */
  const typedAt = new Map<string, number>();
  /** The members chosen from the "@" list for the message being written, per conversation: who, and the name written for them. */
  const picked = new Map<string, Map<string, string>>();
  /** The "@" being typed in the message field: where it starts, what follows it, and which choice is lit. */
  let mention: { conversation: string; at: number; query: string; sel: number } | null = null;
  /** For a conversation opened with a mention in what was unread: the first such message, until it is brought into view. */
  const mentionAt = new Map<string, { id: string }>();
  /** What is typed in the search above the list of conversations. */
  let filter = '';
  /** Your friends, asked for when a search above the list starts, so it finds a friend you have not written to yet. */
  let friends: Person[] | null = null;
  let threadSearchTimer: number | null = null;
  /**
   * Someone's card, opened from a member of a group, a sender's name or a 1:1 conversation's header. `asking` is the question
   * before unfriending or blocking.
   */
  let card: { userId: string; data: PersonCard | null; error: Failure | null; asking: 'unfriend' | 'block' | null; busy: boolean } | null = null;
  /** What each card said last: the "N nhóm chung" under a 1:1 conversation's name comes from here. */
  const cards = new Map<string, PersonCard>();
  /** The question before leaving a group from the side panel. */
  let leaving = false;
  /** The conversation whose "for how long" choices are open, when its bell was tapped to mute it. */
  let muting: string | null = null;
  /** The colour and background being chosen for a conversation (null: each person's own), from its info panel. */
  let looking: { id: string; tab: 'colour' | 'pattern'; palette: string | null; pattern: string | null; busy: boolean; error: Failure | null } | null = null;
  /** An app file whose card was tapped: asked about before it is saved. */
  let appFile: Attachment | null = null;
  /** What is being passed on, while the person picks where to (at most FORWARD_MAX), with the search over the list and a note to send after it. */
  let forwarding: { body: string; attachments: Attachment[]; sticker: string | null; picked: string[]; q: string; note: string; busy: boolean } | null = null;
  /** The panel under the composer, open on its emoji or its sticker tab; the tab last used is remembered on this device. */
  let picker: 'emoji' | 'stickers' | null = null;
  /** The conversation just hidden, while "Hoàn tác" is offered (UNDO_HIDE_MS), and the timer that takes the offer away. */
  let undoHide: { id: string; timer: number } | null = null;
  /** What copying a message said ("copied", or that it could not be), and the timer (COPY_NOTE_MS) that takes it away. It shows just above the message field. */
  let copyNote: { text: string; timer: number } | null = null;
  /** The pack of stickers shown: -1 is "Gần đây" (the ones this device sent lately). */
  let stickerPack = -1;
  /**
   * The owner's packs for the open conversation (a pack may be only in some), asked for the first time its stickers are shown, after
   * Gom's own in the picker; `packsFor` says which conversation they are for.
   */
  let customPacks: CustomPack[] | null = null;
  let packsFor: string | null = null;
  function loadCustomPacks(): void {
    const id = openId;
    if (id === null || (packsFor === id && customPacks !== null)) return;
    packsFor = id; customPacks = [];
    void api.stickerPacks(id).then((r) => {
      if (packsFor !== id) return;
      if (r.ok) { customPacks = r.data.filter((p) => p.stickers.length > 0); update(); } else customPacks = null;
    });
  }
  /** The group of emoji shown: -1 is "Gần đây". */
  let emojiGroup = recentEmoji().length > 0 ? -1 : 0;

  function idFromPath(): string | null { return CONVERSATION_PATH.exec(deps.nav.path())?.[1]?.toLowerCase() ?? null; }
  const chatOf = (id: string | null): Conversation | undefined => (id === null ? undefined : chats?.find((c) => c.id === id));
  const newestSeq = (t: Thread): number => t.messages[t.messages.length - 1]?.seq ?? 0;

  /** What the side panel lists for a conversation, asked of the server a page at a time (not only what the thread has loaded). */
  interface MediaList { items: MediaItem[]; hasMore: boolean; loading: boolean; loaded: boolean }
  const mediaLists = new Map<string, Record<'image' | 'file', MediaList>>();
  function mediaOf(id: string): Record<'image' | 'file', MediaList> {
    let m = mediaLists.get(id);
    if (m === undefined) { m = { image: { items: [], hasMore: false, loading: false, loaded: false }, file: { items: [], hasMore: false, loading: false, loaded: false } }; mediaLists.set(id, m); }
    return m;
  }

  function threadOf(id: string): Thread {
    let t = threads.get(id);
    if (t === undefined) {
      t = { messages: [], pending: [], hasMore: false, loading: false, loaded: false, lastReadAtOpen: 0, unreadAtOpen: 0, jump: false, below: 0, pins: [], pinsOpen: false, editing: null, draft: drafts?.load(id) ?? '', tray: [], replying: null, stick: true, cursor: 0, reads: { enabled: false, seqs: new Map() }, flushing: false, retryTimer: null, failures: 0, attempts: new Map() };
      threads.set(id, t);
    }
    return t;
  }

  // ---- drawing

  /** A round badge: the picture when there is one, else the initials. `size` is '' (48 px), 'sm' or 'xl'. */
  const avatar = (name: string, picture?: string | null, size: '' | 'sm' | 'xl' = ''): TemplateResult =>
    html`<div class="av a${avatarTone(name)} ${size} ${picture ? 'has-pic' : ''}" aria-hidden="true">${picture
      ? html`<img src=${avatarUrl(picture)} alt="" loading="lazy" decoding="async">` : initials(name)}</div>`;
  /** The picture of someone in a group, from its member list (asked for when the group opens). */
  /** A conversation's picture: the other person's in a 1:1 chat, the group's own in a group. */
  const pictureOf = (c: Conversation): string | null | undefined => c.kind === 'group' ? c.avatar : c.peer?.avatar;
  const memberPicture = (conversation: string, userId: string): string | null | undefined => people.get(conversation)?.find((m) => m.id === userId)?.avatar;

  function preview(c: Conversation): string {
    const last = c.lastMessage;
    if (last === null) return say.chat.noMessagesYet;
    if (last.deleted) return recalledText(last.sender.id === me?.id, c.kind === 'group' ? last.sender.displayName : null);
    if (last.call) return callPreview(last.call, me?.id ?? '');
    const who = last.sender.id === me?.id ? `${say.common.you}: ` : c.kind === 'group' ? `${last.sender.displayName}: ` : '';
    return who + (last.body !== '' ? last.body : last.sticker ? say.chat.sentSticker : last.attachment === 'image' ? say.chat.sentPicture : last.attachment === 'video' ? say.chat.sentVideo : last.attachment === 'file' ? say.chat.sentFile : '');
  }

  const row = (c: Conversation) => html`
    <li class="row"><a class="item ${c.unread > 0 || c.markedUnread ? 'unread' : ''} ${c.id === openId ? 'active' : ''}" href="/c/${c.id}" data-chat=${c.id}
      aria-current=${c.id === openId ? 'page' : nothing} @click=${(e: MouseEvent) => onRowClick(e, c.id)}
      @contextmenu=${(e: MouseEvent) => onRowContext(e, c.id)}
      @pointerdown=${(e: PointerEvent) => rowPressStart(e, c.id)} @pointermove=${rowPressMove} @pointerup=${rowPressEnd} @pointercancel=${rowPressEnd} @pointerleave=${rowPressEnd}>
      ${avatar(c.title ?? '?', pictureOf(c))}
      <div class="im">
        <div class="l1">${c.pinned ? html`<span class="pin-mark" role="img" aria-label=${say.chat.pinnedMark}>${icons.pin()}</span>` : nothing}<span class="nm">${c.title ?? '…'}</span>${c.palette ? html`<span class="look-dot" data-palette=${c.palette} role="img" aria-label=${say.chat.ownLook}></span>` : nothing}${c.lastMessage ? html`<span class="tm">${listTime(c.lastMessage.createdAt, deps.now())}</span>` : nothing}</div>
        <div class="l2"><span class="pv ${c.lastMessage?.call && missedByMe(c.lastMessage.call, me?.id ?? '') ? 'missed-call' : ''}">${c.lastMessage?.call ? (c.lastMessage.call.callerId === me?.id ? icons.callOut() : icons.callIn()) : nothing}${preview(c)}</span>
          ${isMuted(c) ? html`<span class="mute-mark" role="img" aria-label=${say.chat.mutedMark}>${icons.bellOff()}</span>` : nothing}
          ${c.mentioned ? html`<span class="badge at" data-mentioned role="img" aria-label=${say.chat.mentionedYou}>@</span>` : nothing}
          ${c.unread > 0 ? html`<span class="badge ${isMuted(c) ? 'muted' : ''}" aria-label=${say.chat.unreadCount(c.unread)}>${c.unread > 99 ? '99+' : c.unread}</span>`
            : c.markedUnread ? html`<span class="badge dot ${isMuted(c) ? 'muted' : ''}" data-marked-unread role="img" aria-label=${say.chat.markUnread}></span>` : nothing}</div>
      </div></a>
      <button class="ib row-more" type="button" data-row-menu=${c.id} aria-label=${say.chat.optionsFor(c.title)} aria-haspopup="menu"
        aria-expanded=${rowMenu?.id === c.id ? 'true' : 'false'} @click=${(e: MouseEvent) => openRowMenu(c.id, menuPoint(e))}>${icons.more()}</button></li>`;

  /** The conversations in view: hidden ones are listed by the server (for a search) and left out here. */
  const shownChats = (): Conversation[] => (chats ?? []).filter((c) => c.hidden !== true);
  /** The conversations whose name matches what is typed above the list (all of them when nothing is typed). */
  const visibleChats = (): Conversation[] => shownChats().filter((c) => matches(c.title ?? '', filter));
  /** Hidden conversations whose name matches what is typed: found by a search only, never listed with nothing typed. */
  const hiddenMatches = (q: string): Conversation[] => q.trim() === '' ? [] : (chats ?? []).filter((c) => c.hidden === true && matches(c.title ?? '', q));
  const hiddenLabel = () => html`<h3 class="hidden-label" data-hidden-label>${say.chat.hiddenLabel}</h3>`;

  const banner = () => offline ? html`<div class="warn" role="status">${icons.wifiOff()}<span>${say.chat.offline}</span></div>` : nothing;

  const empty = () => html`
    <div class="empty">
      <div class="big-icon">${icons.logo()}</div>
      <h2>${say.chat.emptyTitle}</h2>
      <p class="lead">${say.chat.emptyLead}</p>
      <a class="btn" href="/friends">${say.common.addFriends}</a>
    </div>`;

  /** What stands where a message was: in your voice, or theirs (with their name in a group). */
  const recalledText = (mine: boolean, name: string | null): string =>
    mine ? say.chat.recalledByYou : name === null ? say.chat.recalled : say.chat.recalledBy(name);


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
    /** Escape, the shade or "Đóng": the menu moves out first (motion.ts). A choice closes it at once. */
    const dismiss = () => dismissMenu(root, close);
    const pinned = t.pins.some((p) => p.id === m.id);
    // What can still be passed on or saved: a trimmed picture or file cannot.
    const kept = (m.attachments ?? []).filter((a) => !a.removed);
    return html`
      <div class="scrim" data-scrim @click=${dismiss}></div>
      <div class="menu ${mine ? 'mine' : ''}" role="menu" aria-label=${say.chat.messageOptions} @keydown=${(e: KeyboardEvent) => { if (e.key === 'Escape') dismiss(); }}>
      ${menu.hide ? html`
        <p>${say.chat.hideQ}</p>
        <div class="ask"><button class="btn small" type="button" data-confirm-hide @click=${() => void hide(m)}>${say.chat.deleteForMe}</button>
        <button class="btn secondary small" type="button" data-cancel-hide @click=${close}>${say.common.cancel}</button></div>` : menu.confirming ? html`
        <p>${say.chat.recallQ}</p>
        <div class="ask"><button class="btn small" type="button" data-confirm-recall @click=${() => void recall(m)}>${say.chat.recall}</button>
        <button class="btn secondary small" type="button" data-cancel-recall @click=${close}>${say.common.cancel}</button></div>` : html`
        <div class="emoji-row" role="group" aria-label=${say.chat.react}>${EMOJI.map((e) => html`
          <button class="emoji" type="button" data-react=${e} aria-label=${say.chat.reactWith(e)} @click=${() => void setReaction(m, e)}>${e}</button>`)}</div>
        <div class="items-grid">
        ${menuItem('reply', icons.reply(), say.chat.reply, () => startReply(m))}
        ${m.sticker ? nothing : menuItem('copy', icons.copy(), say.chat.copy, () => void copyText(m))}
        ${pinned ? menuItem('unpin', icons.pin(), say.chat.unpin, () => void setPin(m, false)) : menuItem('pin', icons.pin(), say.chat.pin, () => void setPin(m, true))}
        ${m.body === '' && !m.sticker && kept.length === 0 ? nothing : menuItem('forward', icons.forward(), say.chat.forward, () => { menu = null; startForward(m.body, kept, m.sticker ?? null); })}
        ${m.attachments?.length === 1 && kept.length === 1 ? saveItem(kept[0]!) : nothing}
        ${mine && !m.sticker ? menuItem('edit', icons.pencil(), say.chat.edit, () => startEdit(m)) : nothing}
        ${menuItem('hide', icons.trash(), say.chat.deleteForMe, () => { menu = { id: m.id, confirming: false, hide: true }; update(); })}
        ${mine ? menuItem('recall', icons.undo(), say.chat.recall, () => { menu = { id: m.id, confirming: true }; update(); }, true) : nothing}
        </div>
        <button class="btn secondary small close-menu" type="button" data-cancel-recall @click=${dismiss}>${say.common.close}</button>`}
      </div>`;
  };

  /**
   * "Lưu về máy" for a message with one picture or file: a link that downloads it. The menu closes a moment later, once the click
   * has started the download (taking the link away during the click could stop it). Several at once are left to the viewer and
   * the file cards: browsers ask before letting a page download more than one file.
   */
  const saveItem = (a: Attachment): TemplateResult => html`
    <a class="mi" role="menuitem" href=${`/api/attachments/${a.id}`} download=${a.name} data-save
      @click=${() => { window.setTimeout(() => { menu = null; update(); }, 0); }}>${icons.download()}<span>${say.chat.save}</span></a>`;

  /** The reactions under a message: each emoji with how many chose it; yours is marked, and tapping a chip is choosing it or taking yours back. */
  const chips = (m: Shown) => m.deleted || !m.reactions || m.reactions.length === 0 ? nothing : html`
    <div class="reacts">${m.reactions.map((r) => {
      const mine = r.users.some((u) => u.id === me?.id);
      return html`<button class="chip ${mine ? 'mine' : ''}" type="button" data-chip=${r.emoji} aria-pressed=${mine ? 'true' : 'false'}
        aria-label=${`${r.emoji}: ${r.users.map((u) => u.id === me?.id ? say.common.you : u.displayName).join(', ')}`}
        @click=${() => void setReaction(m, mine ? null : r.emoji)}>${r.emoji}<span>${r.users.length}</span></button>`;
    })}</div>`;

  /** What a quote or the reply bar says of a message: its words, or with none, what it carried. */
  const quoted = (r: Reply) => r.snippet !== '' && r.snippet !== null ? r.snippet
    : r.attachment === 'image' ? say.common.picture : r.attachment === 'video' ? say.common.video : r.attachment === 'file' ? say.common.file : '';

  /** What stands above an answer: who wrote the message it answers and the start of it, or that it is no longer there to see. A tap goes to
   * the answered message, as search does. */
  const quote = (r: Reply, conversationId: string) => r.sender === null || r.snippet === null
    ? html`<div class="quote" data-quote=${r.id}><span class="gone">${say.chat.quoteGone}</span></div>`
    : html`<button class="quote" type="button" data-quote=${r.id} aria-label=${say.chat.goToMessage}
        @click=${(e: MouseEvent) => { e.stopPropagation(); void reveal({ id: r.id, conversationId }); }}><b>${r.sender.id === me?.id ? say.common.you : r.sender.displayName}</b>${r.sticker
        ? html`<span class="q-stk"><img src=${stickerUrl(r.sticker)} alt="">${say.common.sticker}</span>` : html`<span>${quoted(r)}</span>`}</button>`;

  /** The words of a message, with every http or https address made into a link that opens in a new tab and tells that site nothing about this one. */
  const words = (body: string, m?: ChatMessage) => linkify(body).flatMap((p) => 'url' in p
    ? [html`<a class="ext" href=${p.url} target="_blank" rel="noopener noreferrer nofollow">${p.text}</a>`] : m === undefined ? [p.text] : withMentions(p.text, m));

  /** Whether a message from someone else names you, or says "@all". */
  const mentionsMe = (m: ChatMessage): boolean => m.sender.id !== me?.id && m.deleted !== true && (m.mentionsAll === true || m.mentions?.includes(me?.id ?? '') === true);

  /**
   * The names a message mentions as they are written ("@" and the member's name now; a member who has left, or changed their name since,
   * is plain words): "@all" and each member named, with who it is. Yours, and "@all", are lit; someone else's is a name that opens their card.
   */
  function mentionTargets(m: ChatMessage): Map<string, { id: string | null; lit: boolean }> {
    const out = new Map<string, { id: string | null; lit: boolean }>();
    if (m.mentionsAll === true) out.set('@all', { id: null, lit: true });
    for (const id of m.mentions ?? []) {
      const name = id === me?.id ? me.displayName : people.get(m.conversationId)?.find((p) => p.id === id)?.displayName;
      if (name !== undefined) out.set(`@${name}`.toLowerCase(), { id, lit: id === me?.id });
    }
    return out;
  }

  /** A run of words cut where it names someone: not run into the word before or after, as the server counts them. */
  function withMentions(text: string, m: ChatMessage): Array<string | TemplateResult> {
    const targets = mentionTargets(m);
    if (targets.size === 0) return [text];
    const names = [...targets.keys()].sort((a, b) => b.length - a.length).map((n) => n.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));
    const pattern = new RegExp(`(?<![\\p{L}\\p{N}_])(?:${names.join('|')})(?![\\p{L}\\p{N}_])`, 'giu');
    const out: Array<string | TemplateResult> = [];
    let from = 0;
    for (const hit of text.matchAll(pattern)) {
      const target = targets.get(hit[0].toLowerCase());
      if (target === undefined) continue;
      if (hit.index > from) out.push(text.slice(from, hit.index));
      out.push(target.lit || target.id === null ? html`<span class="mention lit" data-mention-me>${hit[0]}</span>`
        : html`<button class="mention other" type="button" data-mention=${target.id} @click=${(e: Event) => { e.stopPropagation(); openCard(target.id!); }}>${hit[0]}</button>`);
      from = hit.index + hit[0].length;
    }
    if (from < text.length) out.push(text.slice(from));
    return out;
  }

  /** Moving pictures someone paused or played here; any other plays, unless the device asks for less motion. */
  const movingChoice = new Map<string, boolean>();
  const lessMotion = (): boolean => typeof window.matchMedia === 'function' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const isMoving = (id: string): boolean => movingChoice.get(id) ?? !lessMotion();

  /** A picture's or video's own size, for the CSS to give it its place before it has loaded (the page may set no style attribute). */
  const reserve = (el: Element | undefined, a: Attachment) => {
    if (!(el instanceof HTMLElement) || !a.width || !a.height) return;
    el.style.setProperty('--w', `${a.width}px`);
    el.style.setProperty('--r', String(a.width / a.height));
  };

  /**
   * A GIF in a message: the moving picture while it plays, its still first frame while paused. Anything that moves by itself for more
   * than a few seconds needs a way to stop it, so it always has its own pause button beside the one that opens the viewer.
   */
  const movingPicture = (a: Attachment, i: number, of: number, open: () => void): TemplateResult => {
    const moving = isMoving(a.id);
    return html`
      <div class="pic moving" data-moving=${a.id}>
        <button class="pic-open" type="button" data-pic=${a.id} aria-label=${say.chat.viewPicture(i + 1, of)} @click=${open}>
          <img src=${moving ? `/api/attachments/${a.id}` : `/api/attachments/${a.id}/thumb`} alt=${a.name} decoding="async" width=${a.width ?? nothing} height=${a.height ?? nothing} ${ref((el) => reserve(el, a))}></button>
        <button class="moving-toggle" type="button" data-moving-toggle aria-label=${moving ? say.chat.pauseMoving : say.chat.playMoving}
          @click=${() => { movingChoice.set(a.id, !moving); update(); }}>${moving ? icons.pause() : icons.play()}<span aria-hidden="true">GIF</span></button>
      </div>`;
  };

  /** Videos that this browser could not play: shown as a card to download instead. */
  const unplayable = new Set<string>();

  /**
   * A video in its message: the browser's own player, with its still picture (when its sender's device made one) and how long it is.
   * Only its first moments are fetched before it is played, which also gives a picture to one without a still. One this browser
   * cannot play becomes a download.
   */
  const videoView = (a: Attachment): TemplateResult => unplayable.has(a.id)
    ? html`<a class="filecard" href=${`/api/attachments/${a.id}`} download=${a.name} data-video-download=${a.id}>${icons.film()}
        <span class="fi"><b>${a.name}</b><small>${say.chat.videoCannotPlay}</small></span>${icons.download()}</a>`
    : html`<div class="vid" data-video=${a.id}>
        <video controls playsinline preload="metadata" src=${`/api/attachments/${a.id}`} poster=${`/api/attachments/${a.id}/thumb`}
          width=${a.width ?? nothing} height=${a.height ?? nothing} aria-label=${say.chat.videoLabel(a.name, playLength(a.durationMs))} ${ref((el) => reserve(el, a))}
          @error=${() => { unplayable.add(a.id); update(); }}></video>
        ${a.durationMs ? html`<span class="vid-len" aria-hidden="true">${playLength(a.durationMs)}</span>` : nothing}</div>`;

  /** The pictures of a message as a grid, then its videos, then its files as cards. */
  const attachmentsOf = (m: ChatMessage): TemplateResult | typeof nothing => {
    const all = m.attachments ?? [];
    if (all.length === 0) return nothing;
    const pictures = all.filter((a) => a.kind === 'image');
    const videos = all.filter((a) => a.kind === 'video');
    const files = all.filter((a) => a.kind === 'file');
    return html`
      ${pictures.length === 0 ? nothing : html`<div class=${`pics n${Math.min(pictures.length, 5)}`} data-pics>${pictures.map((a, i) => a.removed
        // Trimmed by the owner: the place stays, saying so; nothing to open.
        ? html`<div class="pic removed" data-pic-removed=${a.id}><span>${say.chat.pictureGone}</span></div>`
        : a.animated ? movingPicture(a, i, pictures.length, () => openViewer(m.conversationId, a.id, m.seq > 0 ? null : m.id))
        : html`
        <button class="pic" type="button" data-pic=${a.id} aria-label=${say.chat.viewPicture(i + 1, pictures.length)} @click=${() => openViewer(m.conversationId, a.id, m.seq > 0 ? null : m.id)}>
          <img src=${`/api/attachments/${a.id}/thumb`} alt=${a.name} loading="lazy" decoding="async" width=${a.width ?? nothing} height=${a.height ?? nothing} ${ref((el) => reserve(el, a))}></button>`)}</div>`}
      ${videos.map((a) => a.removed
        ? html`<div class="pic removed vid" data-video-removed=${a.id}><span>${say.chat.videoGone}</span></div>`
        : videoView(a))}
      ${files.map((a) => a.removed
        ? html`<div class="filecard removed" data-file-removed=${a.id}>${icons.file()}<span class="fi"><b>${say.chat.fileGone}</b><small>${a.name}</small></span></div>`
        : a.audio === true ? audioCard(a)
        : isAppFile(a.name) ? html`
        <a class="filecard" href=${`/api/attachments/${a.id}`} download=${a.name} data-file=${a.id} data-app-file
          @click=${(e: MouseEvent) => { e.preventDefault(); appFile = a; update(); }}>${icons.file()}
          <span class="fi"><b>${a.name}</b><small>${formatSize(a.size)}</small></span>${icons.download()}</a>
        <p class="appwarn" data-app-file-warning>${icons.alert()}<span>${say.chat.appFileWarning}</span></p>`
        : html`
        <a class="filecard" href=${`/api/attachments/${a.id}`} download=${a.name} data-file=${a.id}>${icons.file()}
          <span class="fi"><b>${a.name}</b><small>${formatSize(a.size)}</small></span>${icons.download()}</a>`)}`;
  };

  /** The big picture, with the others of its message one step away. Escape or a click outside closes it. */
  /** One picture in the viewer, with the message it came in. */
  interface Slide { attachment: Attachment; messageId: string; sender: { id: string; displayName: string }; body: string; createdAt: string; sent: boolean }

  /**
   * The pictures the viewer steps through, oldest first: all of the conversation's that the side panel's list has (the list grows
   * as older pages are asked for), or, for a picture the list does not have (yet), only the pictures of its own message.
   */
  function slidesOf(v: NonNullable<typeof viewer>): Slide[] {
    const items = mediaOf(v.conversation).image.items;
    if (v.local === null && items.some((i) => i.attachment.id === v.attachmentId)) {
      // The list is newest message first; a stable sort keeps the sender's order inside one message.
      return [...items].sort((a, b) => a.seq - b.seq).map((i) => ({ attachment: i.attachment, messageId: i.messageId, sender: i.sender, body: i.body, createdAt: i.createdAt, sent: true }));
    }
    const t = threadOf(v.conversation);
    const m = [...t.messages, ...t.pending].find((x) => v.local !== null ? x.id === v.local : x.attachments?.some((a) => a.id === v.attachmentId));
    if (m === undefined) return [];
    return (m.attachments ?? []).filter((a) => a.kind === 'image' && !a.removed)
      .map((a) => ({ attachment: a, messageId: m.id, sender: m.sender, body: m.body, createdAt: m.createdAt, sent: m.seq > 0 }));
  }

  /** Opens the viewer on a picture. A sent one is looked up in the conversation's list, so the viewer can go on to the other messages' pictures. */
  function openViewer(conversation: string, attachmentId: string, local: string | null): void {
    viewer = { conversation, attachmentId, local };
    viewerSync = true;
    update();
    if (local === null) void findInList(conversation, attachmentId);
  }

  function closeViewer(): void {
    viewer = null;
    viewerSync = false;
    update();
  }

  /**
   * Brings the conversation's pictures back to this one, in one request after the first page (or none, when it is there). Until then,
   * or when it is further back than the server goes in one jump, the viewer shows its message's pictures only.
   */
  async function findInList(conversation: string, attachmentId: string): Promise<void> {
    const list = mediaOf(conversation).image;
    if (!list.loaded) await loadMedia(conversation, 'image', false, attachmentId);
    if (viewer?.attachmentId !== attachmentId || !list.hasMore || list.items.some((i) => i.attachment.id === attachmentId)) return;
    await loadMedia(conversation, 'image', true, attachmentId);
  }

  /** Moves the viewer one picture back (-1) or on (1). At the oldest picture with older ones on the server, they are asked for first. */
  async function stepViewer(by: -1 | 1): Promise<void> {
    if (viewer === null) return;
    const v = viewer;
    let slides = slidesOf(v);
    let i = slides.findIndex((s) => s.attachment.id === v.attachmentId);
    const list = mediaOf(v.conversation).image;
    if (by < 0 && i === 0 && v.local === null && list.hasMore && slides.length === list.items.length) {
      await loadMedia(v.conversation, 'image', true);
      if (viewer !== v) return;
      slides = slidesOf(v);
      i = slides.findIndex((s) => s.attachment.id === v.attachmentId);
    }
    const next = slides[i + by];
    if (next === undefined) return;
    v.attachmentId = next.attachment.id;
    viewerSync = true;
    update();
  }

  /** A small picture in the strip under the viewer was chosen: that one, at once. At the oldest, older ones are asked for. */
  function jumpViewer(attachmentId: string): void {
    const v = viewer;
    if (v === null || v.attachmentId === attachmentId) return;
    v.attachmentId = attachmentId;
    viewerSync = true;
    update();
    const list = mediaOf(v.conversation).image;
    const slides = slidesOf(v);
    if (v.local === null && slides[0]?.attachment.id === attachmentId && list.hasMore && slides.length === list.items.length) void loadMedia(v.conversation, 'image', true);
  }

  /** Where the strip of pictures came to rest after a swipe: that picture is the current one. Near the oldest, older ones are asked for. */
  function onViewerSettle(track: HTMLElement): void {
    const v = viewer;
    if (v === null || track.clientWidth <= 0) return;
    const slides = slidesOf(v);
    const s = slides[Math.round(track.scrollLeft / track.clientWidth)];
    if (s !== undefined && s.attachment.id !== v.attachmentId) { v.attachmentId = s.attachment.id; update(); }
    const list = mediaOf(v.conversation).image;
    if (v.local === null && slides[0]?.attachment.id === v.attachmentId && list.hasMore && slides.length === list.items.length) void loadMedia(v.conversation, 'image', true);
  }

  let settleTimer: number | null = null;
  /** Where "scrollend" is missing, a pause in scrolling stands in for it. */
  function onViewerScroll(track: HTMLElement): void {
    if (settleTimer !== null) deps.timer.clear(settleTimer);
    settleTimer = deps.timer.set(() => { settleTimer = null; onViewerSettle(track); }, 150);
  }

  /** The time a picture was sent, as the viewer says it: "22:22" today, "22:22 Hôm qua" or "22:22 3/12" before. */
  const sentAt = (iso: string): string => { const day = listTime(iso, deps.now()); return day === clock(iso) ? day : `${clock(iso)} ${day}`; };

  /**
   * The picture shown large, over everything: a modal dialog, so Escape, and the Back gesture on a phone, close it instead of
   * leaving the conversation. The pictures sit in a strip that the browser scrolls and snaps one at a time (a swipe on a phone);
   * the arrows and the arrow keys step too.
   */
  const viewerView = () => {
    if (viewer === null || openId === null || viewer.conversation !== openId) return nothing;
    const v = viewer;
    const slides = slidesOf(v);
    const i = slides.findIndex((s) => s.attachment.id === v.attachmentId);
    const current = slides[i];
    if (current === undefined) return nothing;   // its message was taken back or deleted for you
    const list = mediaOf(v.conversation).image;
    const older = v.local === null && list.hasMore && slides.length === list.items.length;
    const who = current.sender.id === me?.id ? say.common.you : current.sender.displayName;
    return html`
      <dialog class="viewer" aria-label=${say.chat.viewer} data-viewer data-modal
        @close=${closeViewer}
        @keydown=${(e: KeyboardEvent) => {
          if (e.key === 'Escape') { e.preventDefault(); closeViewer(); }
          else if (e.key === 'ArrowLeft') { e.preventDefault(); void stepViewer(-1); }
          else if (e.key === 'ArrowRight') { e.preventDefault(); void stepViewer(1); }
        }}>
        <div class="viewer-bar">
          <span data-viewer-count aria-live="polite">${i + 1} / ${slides.length}${older ? '+' : ''}</span>
          <a class="ib" href=${`/api/attachments/${current.attachment.id}`} download=${current.attachment.name} aria-label=${say.chat.save} data-viewer-download>${icons.download()}</a>
          ${current.sent ? html`
            <button class="ib" type="button" data-viewer-forward aria-label=${say.chat.forwardPicture} @click=${() => { closeViewer(); startForward('', [current.attachment]); }}>${icons.forward()}</button>
            <button class="ib" type="button" data-viewer-goto aria-label=${say.chat.goToMessage} @click=${() => { closeViewer(); void reveal({ id: current.messageId, conversationId: v.conversation }); }}>${icons.reply()}</button>` : nothing}
          <button class="ib" type="button" data-viewer-close aria-label=${say.common.close} @click=${closeViewer}>${icons.close()}</button>
        </div>
        ${i > 0 || older ? html`<button class="ib vprev" type="button" data-viewer-prev aria-label=${say.chat.previousPicture} @click=${() => void stepViewer(-1)}>${icons.back()}</button>` : nothing}
        ${i < slides.length - 1 ? html`<button class="ib vnext" type="button" data-viewer-next aria-label=${say.chat.nextPicture} @click=${() => void stepViewer(1)}>${icons.next()}</button>` : nothing}
        <div class="vtrack" data-viewer-track tabindex="0" aria-label=${say.chat.pictures}
          @scrollend=${SCROLL_END ? (e: Event) => onViewerSettle(e.currentTarget as HTMLElement) : nothing}
          @scroll=${SCROLL_END ? nothing : (e: Event) => onViewerScroll(e.currentTarget as HTMLElement)}
          @click=${(e: MouseEvent) => { if ((e.target as Element).classList.contains('vslide')) closeViewer(); }}>
          ${repeat(slides, (s) => s.attachment.id, (s, k) => html`
            <div class="vslide" role="group" aria-roledescription=${say.chat.pictureRole} aria-label=${`${k + 1} / ${slides.length}`} data-viewer-slide=${s.attachment.id}>
              ${s.attachment.kind === 'video'
                ? html`<video class="vimg" controls playsinline preload="none" src=${`/api/attachments/${s.attachment.id}`} poster=${`/api/attachments/${s.attachment.id}/thumb`}
                    width=${s.attachment.width ?? nothing} height=${s.attachment.height ?? nothing} aria-label=${say.chat.videoLabel(s.attachment.name, playLength(s.attachment.durationMs))}></video>`
                : html`<img class="vimg" src=${`/api/attachments/${s.attachment.id}`} alt=${s.attachment.name} loading="lazy" decoding="async"
                width=${s.attachment.width ?? nothing} height=${s.attachment.height ?? nothing}>`}</div>`)}
        </div>
        <div class="viewer-foot" data-viewer-foot>
          <div class="vwho"><b>${who}</b><small>${sentAt(current.createdAt)}</small></div>
          ${current.body === '' ? nothing : html`<p class="vcap" data-viewer-caption>${words(current.body)}</p>`}
        </div>
        ${slides.length < 2 ? nothing : html`
          <div class="vthumbs" data-viewer-thumbs role="group" aria-label=${say.chat.pictures}>
            ${repeat(slides, (s) => s.attachment.id, (s, k) => html`
              <button class="vthumb" type="button" data-viewer-thumb=${s.attachment.id} aria-label=${`${k + 1} / ${slides.length}`} aria-current=${k === i ? 'true' : nothing}
                @click=${() => jumpViewer(s.attachment.id)}><img src=${`/api/attachments/${s.attachment.id}/thumb`} alt="" loading="lazy" decoding="async">
                ${s.attachment.kind === 'video' ? html`<span class="tray-play" aria-hidden="true">${icons.play()}</span>` : nothing}</button>`)}
          </div>`}
      </dialog>`;
  };

  /** What is chosen for the next message, above the box where it is written. */
  const trayView = (t: Thread) => t.tray.length === 0 ? nothing : html`
    <div class="atray" role="group" aria-label=${say.chat.tray} data-tray>
      ${repeat(t.tray, (i) => i.key, (i) => html`
        <div class=${`tray-item ${i.state}`} data-tray-item=${i.key}>
          ${i.state === 'done' && i.attachment?.kind === 'image' ? html`<img src=${`/api/attachments/${i.attachment.id}/thumb`} alt=${i.name}>`
            : i.preview !== undefined ? html`<img src=${i.preview} alt=${i.name}><span class="tray-play" aria-hidden="true">${icons.play()}</span>`
            : html`<div class="tray-file">${icons.file()}<span>${i.name}</span></div>`}
          ${i.state === 'converting' ? html`<progress max="1" value=${i.progress} aria-label=${say.chat.preparingVideo(i.name)} data-tray-converting></progress>` : nothing}
          ${i.state === 'uploading' || i.state === 'waiting' ? html`<progress max="1" value=${i.state === 'waiting' ? 0 : i.progress} aria-label=${say.chat.uploading(i.name)}></progress>` : nothing}
          ${i.state === 'failed' ? html`<div class="tray-err" role="alert">${i.error ? errorText(i.error) : say.chat.uploadFailed}
            <button class="btn secondary small" type="button" data-tray-retry @click=${() => retryTray(i)}>${say.common.retry}</button></div>` : nothing}
          <button class="ib tray-x" type="button" data-tray-remove aria-label=${say.chat.removeFile(i.name)} @click=${() => { t.tray = t.tray.filter((x) => x !== i); update(); }}>${icons.close()}</button>
        </div>`)}
    </div>`;

  const bubble = (item: Extract<ThreadItem, { kind: 'msg' }>, group: boolean, seen: string | null, tick: Tick): TemplateResult => {
    const m = item.msg;
    const others = group && !item.mine;
    const classes = `mrow ${item.mine ? 'out' : 'in'} ${item.first ? 'first' : ''} ${item.last ? 'last' : ''} ${m.deleted ? 'recalled' : ''} ${m.sticker && !m.deleted ? 'sticker' : ''} ${!m.deleted && !m.sticker && !m.reply && !m.attachments?.length && onlyEmoji(m.body) ? 'jumbo' : ''} ${found === m.id ? 'found' : ''} ${fresh.has(m.id) ? 'fresh' : ''} ${mentionsMe(m) ? 'mentioned' : ''}`;
    // A message that has arrived has a menu (answer, copy, and for your own, take back): not one still on its way, nor one taken back.
    const hasMenu = m.state === undefined && m.deleted !== true;
    // A phone: a round arrow that comes in beside the message as it is dragged (see swipeMove).
    const swipeIcon = hasMenu && deps.coarsePointer() ? html`<span class="swipe-ico" aria-hidden="true">${icons.reply()}</span>` : nothing;
    const tools = hasMenu ? html`
      <div class="tools" role="group" aria-label=${say.chat.messageOptions}>
        <button class="ib small" type="button" data-quick-reply aria-label=${say.chat.reply} @click=${() => startReply(m)}>${icons.reply()}</button>
        <button class="ib small menu-btn" type="button" data-menu aria-label=${say.chat.messageOptions} aria-haspopup="menu" aria-expanded=${menu?.id === m.id ? 'true' : 'false'}
          @click=${(e: MouseEvent) => { menu = menu?.id === m.id ? null : { id: m.id, confirming: false, at: pointAt(e) }; update(); }}>${icons.more()}</button>
      </div>` : nothing;
    // The time shows on the last message of a run; the others show it when held over (title) and to a screen reader.
    const sending = m.state === 'sending' || m.state === 'queued';
    const showTime = item.last || sending || m.editedAt !== undefined && m.editedAt !== null && !m.deleted;
    return html`
      <div class=${classes} data-msg=${m.id} data-state=${m.state ?? 'sent'}>
        ${swipeIcon}
        ${item.mine ? tools : nothing}
        ${others ? (item.last ? html`<button class="av-btn" type="button" data-avatar=${m.sender.id} aria-label=${m.sender.displayName} @click=${() => openCard(m.sender.id)}>${avatar(m.sender.displayName, memberPicture(m.conversationId, m.sender.id), 'sm')}</button>` : html`<div class="sp"></div>`) : nothing}
        <div class="bcol">
        <div class="bub" title=${clock(m.createdAt)} @contextmenu=${(e: MouseEvent) => onContext(e, m, hasMenu)} @pointerdown=${(e: PointerEvent) => { pressStart(e, m, hasMenu); swipeStart(e, m, item.mine, hasMenu); }}
          @pointermove=${(e: PointerEvent) => { pressMove(e); swipeMove(e); }} @pointerup=${(e: PointerEvent) => { swipeEnd(true); tapEnd(e, m); pressEnd(); }}
          @pointercancel=${() => { swipeEnd(false); pressEnd(); }} @pointerleave=${pressEnd}>${others && item.first ? html`<button class="sender" type="button" data-sender=${m.sender.id} @click=${(e: MouseEvent) => { e.stopPropagation(); openCard(m.sender.id); }}>${m.sender.displayName}</button>` : nothing}${m.deleted ? html`<span class="txt gone">${recalledText(item.mine, others ? m.sender.displayName : null)}</span>` : html`${m.reply ? quote(m.reply, m.conversationId) : nothing}${m.sticker ? html`<img class="stk" data-sticker=${m.sticker} src=${stickerUrl(m.sticker)} alt=${say.common.stickerNamed(stickerName(m.sticker))} width="120" height="120" draggable="false">` : nothing}${attachmentsOf(m)}${m.body === '' ? nothing : html`<span class="txt">${words(m.body, m)}</span>`}`}
          ${showTime ? html`<span class="ts">${m.editedAt && !m.deleted ? html`<span class="edited">${say.chat.edited}</span>` : nothing}${sending ? html`<span class="sending" role="img" aria-label=${m.state === 'sending' ? say.chat.sending : say.chat.waiting}>${icons.clock()}</span>` : clock(m.createdAt)}${tick === null ? nothing : html` <span class=${`tick t-${tick}`} role="img" aria-label=${tick === 'seen' ? say.chat.seen : say.chat.delivered}>${tick === 'seen' ? icons.checks() : icons.check()}</span>`}</span>` : html`<span class="sr-only">${clock(m.createdAt)}</span>`}</div>
        ${chips(m)}
        </div>
        ${!item.mine ? tools : nothing}
      </div>
      ${seen === null ? nothing : html`<div class="seen">${icons.checks()}<span>${seen}</span></div>`}
      ${m.state === 'queued' ? html`<div class="queued-note">${m.error?.error === 'network' || m.error === undefined ? say.chat.willSendOnline : say.chat.willRetry}</div>` : nothing}
      ${m.state === 'failed' ? html`
        <div class="failed" role="alert">${icons.alert()}<span>${m.error ? errorText(m.error) : say.chat.notSent}</span>
          <button class="btn secondary small" type="button" data-retry @click=${() => retry(m.conversationId, m.clientId)}>${say.chat.resend}</button>
          <button class="btn secondary small" type="button" data-discard @click=${() => discard(m.conversationId, m.clientId)}>${say.chat.discard}</button></div>` : nothing}`;
  };

  /** Whether this chat can be called from here: calls on, ready here, and a one-to-one chat with a friend (their ID is there only then). */
  const callable = (c: Conversation | undefined): c is Conversation =>
    me?.calls === true && deps.calls?.ready() === true && c?.kind === 'direct' && c.peer !== null && !c.peer.deleted && c.peer.gomId !== null;
  const callIn = (c: Conversation) => { void deps.calls?.start(c.id, { name: c.title ?? c.peer?.displayName ?? '', avatar: c.peer?.avatar }); };

  /** A call's line (mockup 29): a card on the side of whoever called, worded for the reader, a missed one in red with "Gọi lại". */
  const callCard = (m: Shown, call: CallLine) => {
    const mine = call.callerId === me?.id;
    const missed = missedByMe(call, me?.id ?? '');
    const c = chatOf(m.conversationId);
    return html`<div class="call-row ${mine ? 'out' : 'in'}" data-call-line=${m.id} data-outcome=${call.outcome}>
      <div class="call-card ${missed ? 'missed' : ''}">
        <span class="call-i">${icons.call()}</span>
        <span class="call-t"><b>${callTitle(call, me?.id ?? '')}</b>
          <small>${mine ? icons.callOut() : icons.callIn()}<span class="sr-only">${mine ? say.calls.outgoing : say.calls.incoming}</span>${call.outcome === 'completed' ? `${callLength(call.seconds)} · ` : ''}${clock(call.startedAt)}</small></span>
        ${missed && callable(c) ? html`<button class="call-back" type="button" data-call-back @click=${() => callIn(c)}>${say.calls.callBack}</button>` : nothing}
      </div></div>`;
  };

  const threadItem = (item: ThreadItem, group: boolean, seenText: (msg: Shown) => string | null, tick: (msg: Shown) => Tick) => {
    if (item.kind === 'day') return html`<div class="day">${item.label}</div>`;
    if (item.kind === 'new') return html`<div class="newdiv"><span>${say.chat.newMessages(item.count)}</span></div>`;
    if (item.kind === 'notice' && item.msg.notice?.call) return callCard(item.msg, item.msg.notice.call);
    if (item.kind === 'notice') return html`<div class="notice-row" data-notice=${item.msg.id} role="status"><span>${noticeText(item.msg, me?.id ?? '')}</span></div>`;
    return bubble(item, group, seenText(item.msg), tick(item.msg));
  };

  /** The mark on your own message: one check once it has gone out, two once it was seen (in a group, by everyone else). Nothing when you hide your reading, or for what is not yours. */
  function tickOf(t: Thread, c: Conversation | undefined): (msg: Shown) => Tick {
    const others = c?.kind === 'group' ? Math.max(1, (c.memberCount ?? 2) - 1) : 1;
    return (msg) => {
      if (msg.sender.id !== me?.id || msg.state !== undefined || msg.deleted === true || msg.notice) return null;
      const readers = t.reads.enabled ? [...t.reads.seqs.values()].filter((seq) => seq >= msg.seq).length : 0;
      return readers >= others ? 'seen' : 'sent';
    };
  }

  /** The "seen" line goes under your last sent message that has not been taken back, once someone has read up to it. */
  function seenText(t: Thread, group: boolean): (msg: Shown) => string | null {
    const lastMine = [...t.messages].reverse().find((m) => m.sender.id === me?.id && m.deleted !== true && !m.notice);
    const count = lastMine === undefined ? 0 : [...t.reads.seqs.values()].filter((seq) => seq >= lastMine.seq).length;
    return (msg) => group && lastMine !== undefined && msg.id === lastMine.id && count > 0 ? say.chat.seenBy(count) : null;
  }

  /** Looking for words in this conversation: a field, and the messages that contain them, newest first. */
  const searchPanel = (s: NonNullable<typeof searching>) => html`
    <div class="searchbar" role="search">
      <form data-search-form @submit=${(e: Event) => { e.preventDefault(); searchNow(); }}>
        <div class="searchfield input">${icons.search()}
          <input type="search" name="q" aria-label=${say.chat.searchWords} placeholder=${say.chat.searchMessages} autocomplete="off" maxlength=${limits().searchChars} enterkeyhint="search"
            .value=${liveValue(s.q)} @input=${(e: Event) => { s.q = (e.target as HTMLInputElement).value; searchSoon(s); }}
            @keydown=${(e: KeyboardEvent) => { if (e.key === 'Escape') { searching = null; update(); } }}>
          <button class="clear" type="button" data-close-search aria-label=${say.chat.closeSearch} @click=${() => { searching = null; update(); }}>${icons.close()}</button>
        </div>
      </form>
      ${s.results === null ? nothing : s.results.length === 0
        ? html`<p class="hint" data-no-results>${say.chat.noMessagesFound}</p>`
        : html`<ul class="results">${s.results.map((r) => html`
          <li><button type="button" class="result" data-result=${r.id} @click=${() => void reveal(r)}>
            <b>${r.sender.id === me?.id ? say.common.you : r.sender.displayName}</b><small>${listTime(r.createdAt, deps.now())}</small>
            <span>${marked(markedExcerpt(r.body, s.asked, CLIP.result))}</span></button></li>`)}</ul>`}
    </div>`;
  /** The words a search found, in bold. */
  const marked = (runs: Run[]) => runs.map((r) => r.hit ? html`<mark>${r.text}</mark>` : r.text);
  /** The pinned messages: the newest one stays under the header, and a button opens them all. */
  const pinBar = (t: Thread) => t.pins.length === 0 ? nothing : html`
    <div class="pinbar" data-pinbar>
      <button class="pin-top" type="button" data-pin-top @click=${() => void reveal(t.pins[0]!)}>
        ${icons.pin()}<span class="pin-text"><b>${say.chat.pinned}</b><span>${t.pins[0]!.sticker ? say.common.sticker : clip(t.pins[0]!.body, CLIP.pin)}</span></span></button>
      ${t.pins.length > 1 ? html`<button class="btn secondary small" type="button" data-pin-all aria-expanded=${t.pinsOpen ? 'true' : 'false'}
        @click=${() => { t.pinsOpen = !t.pinsOpen; update(); }}>${t.pins.length} tin</button>` : nothing}
    </div>
    ${t.pinsOpen ? html`<ul class="pinlist" data-pinlist>${t.pins.map((p) => html`
      <li><button type="button" class="result" data-pin-item=${p.id} @click=${() => void reveal(p)}>
        <b>${p.sender.id === me?.id ? say.common.you : p.sender.displayName}</b><small>${listTime(p.createdAt, deps.now())}</small>
        <span>${p.sticker ? say.common.sticker : clip(p.body, CLIP.result)}</span></button></li>`)}</ul>` : nothing}`;

  /**
   * Where to pass a message on: a dialog with your conversations (newest first, narrowed by what is typed), up to FORWARD_MAX ticked,
   * and a note that is sent after it. It goes as a new message from you, with nothing saying who wrote it first.
   */
  const forwardPanel = (f: NonNullable<typeof forwarding>) => {
    const shown = shownChats().filter((c) => matches(c.title ?? '', f.q));
    // A hidden one ticked stays in view after the search is cleared, so what is ticked is always seen.
    const found = (chats ?? []).filter((c) => c.hidden === true && (hiddenMatches(f.q).includes(c) || f.picked.includes(c.id)));
    const full = f.picked.length >= FORWARD_MAX;
    const pictures = f.attachments.filter((a) => a.kind === 'image').length;
    const files = f.attachments.length - pictures;
    const what = [pictures > 0 ? say.chat.pictureCount(pictures) : '', files > 0 ? say.chat.fileCount(files) : ''].filter((x) => x !== '').join(', ');
    const cancel = () => { if (!f.busy) { forwarding = null; update(); } };
    const pickRow = (c: Conversation) => {
      const on = f.picked.includes(c.id);
      return html`<li><label class="fwd-row">
        <input type="checkbox" data-forward-pick=${c.id} .checked=${on} ?disabled=${f.busy || (!on && full)}
          @change=${(e: Event) => { const yes = (e.target as HTMLInputElement).checked; f.picked = yes ? [...f.picked.filter((x) => x !== c.id), c.id] : f.picked.filter((x) => x !== c.id); update(); }}>
        ${avatar(c.title ?? '?', pictureOf(c))}<span>${c.title ?? '…'}</span></label></li>`;
    };
    return html`
      <dialog class="fwd" aria-labelledby="fwd-title" data-forward-panel data-modal @close=${cancel}
        @keydown=${(e: KeyboardEvent) => { if (e.key === 'Escape') { e.preventDefault(); cancel(); } }}>
        <header class="fwd-head"><h2 id="fwd-title">${say.chat.forward}</h2>
          <button class="ib" type="button" data-close-forward aria-label=${say.common.close} @click=${cancel}>${icons.close()}</button></header>
        <div class="fwd-what">
          ${f.sticker === null ? nothing : html`<img class="fw-stk" src=${stickerUrl(f.sticker)} alt=${say.common.stickerNamed(stickerName(f.sticker))} width="64" height="64">`}
          ${f.body === '' ? nothing : html`<p class="fw-text">${clip(f.body, CLIP.forward)}</p>`}
          ${what === '' ? nothing : html`<p class="hint">${what}</p>`}
        </div>
        <div class="searchfield input">${icons.search()}
          <input type="search" aria-label=${say.common.searchChats} placeholder=${say.common.search} autocomplete="off" maxlength=${limits().nameChars} data-forward-search
            .value=${liveValue(f.q)} @input=${(e: Event) => { f.q = (e.target as HTMLInputElement).value; update(); }}></div>
        ${shown.length === 0 && found.length === 0 ? html`<p class="hint" data-forward-none>${say.chat.noChatsFound}</p>` : html`
          <div class="fwd-list">
          ${shown.length === 0 ? nothing : html`<ul class="rows">${repeat(shown, (c) => c.id, pickRow)}</ul>`}
          ${found.length === 0 ? nothing : html`${hiddenLabel()}<ul class="rows" data-hidden-list>${repeat(found, (c) => c.id, pickRow)}</ul>`}</div>`}
        ${full ? html`<p class="hint" data-forward-full>${say.chat.forwardMax(FORWARD_MAX)}</p>` : nothing}
        <textarea class="fwd-note" rows="2" aria-label=${say.chat.forwardNote} placeholder=${say.chat.forwardNotePlaceholder} maxlength=${limits().messageChars} data-forward-note
          .value=${liveValue(f.note)} @input=${(e: Event) => { f.note = (e.target as HTMLTextAreaElement).value; }}></textarea>
        <footer class="fwd-foot">
          <button class="btn secondary" type="button" data-cancel-forward ?disabled=${f.busy} @click=${cancel}>${say.common.cancel}</button>
          <button class="btn" type="button" data-forward-send ?disabled=${f.busy || f.picked.length === 0} @click=${() => void sendForward()}>
            ${say.chat.forward}${f.picked.length > 0 ? ` (${f.picked.length})` : ''}</button>
        </footer>
      </dialog>`;
  };

  /** The narrow bar on a wide screen: messages, contacts, and a gear for settings and signing out. */
  const rail = () => html`
    <nav class="rail" aria-label=${say.chat.navigation}>
      <a class="rail-i active" href="/" aria-label=${say.chat.messages} aria-current="page" @click=${onHome}>${icons.logo()}</a>
      <a class="rail-i" href="/friends" aria-label=${say.common.contacts}>${icons.users()}</a>
      <button class="rail-i gear" type="button" data-gear aria-label=${say.chat.settingsAndAccount} aria-haspopup="menu" aria-expanded=${gearOpen ? 'true' : 'false'}
        @click=${() => { gearOpen = !gearOpen; update(); }}>${icons.gear()}</button>
    </nav>
    ${gearOpen ? html`
      <div class="scrim" data-gear-scrim @click=${() => dismissMenu(root, () => { gearOpen = false; update(); })}></div>
      <div class="gearmenu" role="menu" aria-label=${say.chat.account} @keydown=${(e: KeyboardEvent) => { if (e.key === 'Escape') dismissMenu(root, () => { gearOpen = false; update(); }); }}>
        <a class="mi" role="menuitem" href="/settings">${icons.gear()}<span>${say.common.settings}</span></a>
        ${me?.isOwner ? html`<a class="mi" role="menuitem" href="/settings/invites">${icons.users()}<span>${say.common.ownerPage}</span></a>` : nothing}
        <button class="mi" type="button" role="menuitem" data-signout @click=${() => void signOut()}>${icons.signOut()}<span>${say.common.signOut}</span></button>
      </div>` : nothing}`;

  async function signOut(): Promise<void> {
    const result = await api.signOut();
    if (result.ok || result.error === 'unauthorized') go('/sign-in'); else { gearOpen = false; handleFailure(result); }
  }

  /** A phone: opens the information page as a step in the history, so the back gesture closes it. */
  function openInfoPage(): void {
    if (openId === null) return;
    infoPage = true; infoClearing = false; leaving = false;
    deps.nav.push(`/c/${openId}#info`);
    void loadPeople(openId); void loadMedia(openId, 'image'); void loadMedia(openId, 'file');
    update();
  }

  /** The page's own back button: a step back in the history, as the back gesture is (the step back closes it). */
  function backFromInfoPage(): void { deps.nav.back(); }

  /** Closed by something done on it: its step in the history becomes the conversation again, so back does not reopen it. */
  function closeInfoPage(): void {
    if (!infoPage) return;
    infoPage = false; infoClearing = false;
    if (openId !== null) deps.nav.replace(`/c/${openId}`);
    update();
  }

  let infoLeaving = false;
  function toggleInfo(): void {
    if (infoLeaving) return;
    if (infoOpen) {
      // Closing slides it out first (motion.ts); a second press meanwhile does nothing.
      infoLeaving = true;
      leaveInfo(root.querySelector('.thread-pane ~ [data-info-panel]'), () => { infoLeaving = false; setInfo(false); });
      return;
    }
    setInfo(true);
  }

  function setInfo(open: boolean): void {
    infoOpen = open;
    try { localStorage.setItem('gom-info', infoOpen ? '1' : '0'); } catch { /* it is just not remembered */ }
    if (infoOpen && openId !== null) { void loadPeople(openId); void loadMedia(openId, 'image'); void loadMedia(openId, 'file'); }
    update();
  }

  async function loadPeople(id: string): Promise<void> {
    if (chatOf(id)?.kind !== 'group' || people.has(id)) return;
    const result = await api.groupMembers(id);
    if (!result.ok) return;
    people.set(id, result.data);
    update();
  }

  /** Asks the server for a page of the side panel's list: the first (once), or the next one after what is shown. A failure leaves the list as it is. */
  /** A page of the list; `more`: the one after what is here; `to`: every one back to that picture at once. */
  async function loadMedia(id: string, kind: 'image' | 'file', more = false, to?: string): Promise<void> {
    const list = mediaOf(id)[kind];
    if (list.loading || (more ? !list.hasMore : list.loaded)) return;
    list.loading = true;
    update();
    const result = await api.media(id, kind, more ? list.items[list.items.length - 1]?.seq : undefined, to);
    list.loading = false;
    if (result.ok) {
      list.items = mergeMedia(list.items, result.data.items);
      list.hasMore = result.data.hasMore;
      list.loaded = true;
      // Older pictures come in front of the one the viewer shows: the strip is moved so the same picture stays in view.
      if (kind === 'image' && viewer?.conversation === id) viewerSync = true;
    }
    update();
  }

  /** The pictures and files of the open conversation, in the side panel. A new page comes with "Xem thêm". */
  const media = (id: string) => {
    const { image, file } = mediaOf(id);
    const more = (kind: 'image' | 'file', list: MediaList) => !list.hasMore ? nothing
      : html`<button class="btn secondary small" type="button" data-info-more=${kind} ?disabled=${list.loading} @click=${() => void loadMedia(id, kind, true)}>${say.common.seeMore}</button>`;
    return html`
      ${image.items.length === 0 ? nothing : html`<h3>${say.chat.picturesHeading}</h3>
        <div class="info-media" data-info-media>${image.items.map(({ attachment: a }) => html`
          <button class=${`pic ${a.kind === 'video' ? 'vtile' : ''}`} type="button" data-info-pic=${a.id} aria-label=${a.kind === 'video' ? say.chat.videoLabel(a.name, playLength(a.durationMs)) : a.name} @click=${() => openViewer(id, a.id, null)}><img src=${`/api/attachments/${a.id}/thumb`} alt="" loading="lazy">${a.kind === 'video' ? html`<span class="tray-play" aria-hidden="true">${icons.play()}</span>` : nothing}</button>`)}</div>
        ${more('image', image)}`}
      ${file.items.length === 0 ? nothing : html`<h3>${say.chat.filesHeading}</h3>
        <ul class="info-list" data-info-files>${file.items.map(({ attachment: a }) => html`<li><a class="filecard" href=${`/api/attachments/${a.id}`} download=${a.name}>${icons.file()}<span class="fi"><b>${a.name}</b><small>${formatSize(a.size)}</small></span></a></li>`)}</ul>
        ${more('file', file)}`}`;
  };

  /**
   * The panel beside a thread: who it is, ways to find things in it, what is pinned, and who is in a group. As a phone's page
   * (`page`) it also ends with what the list's menu offers for the conversation.
   */
  const infoPanel = (c: Conversation, t: Thread, page = false) => html`
    <aside class="info" aria-label=${say.chat.info} data-info-panel>
      <div class="info-top">
        ${avatar(c.title ?? '?', pictureOf(c), 'xl')}
        <h2>${c.title ?? '…'}</h2>
        <small>${c.kind === 'group' ? say.common.members(c.memberCount ?? 0) : c.peer ? peerLine(c) : ''}</small>
      </div>
      <div class="info-actions">
        <button class="btn secondary small" type="button" data-info-search @click=${() => { if (page) closeInfoPage(); searching ??= { q: '', results: null, asked: '', ticket: 0 }; update(); root.querySelector<HTMLInputElement>('.searchbar input')?.focus(); }}>${icons.search()} ${say.chat.searchMessages}</button>
        <button class="btn secondary small" type="button" data-info-mute @click=${() => onBell(c)}>${isMuted(c) ? icons.bellOff() : icons.bell()} ${isMuted(c) ? say.common.unmute : say.common.mute}</button>
        <button class="btn secondary small" type="button" data-info-look @click=${() => { looking = { id: c.id, tab: 'colour', palette: c.palette ?? null, pattern: c.pattern ?? null, busy: false, error: null }; update(); }}>${icons.palette()} ${say.chat.look}</button>
      </div>
      ${isMuted(c) && c.mutedUntil ? html`<p class="hint" data-muted-until>${say.chat.mutedUntil(clock(c.mutedUntil))}</p>` : nothing}
      ${disappearRow(c)}
      ${t.pins.length === 0 ? nothing : html`
        <h3>${say.chat.pinned}</h3>
        <ul class="info-list">${t.pins.map((p) => html`<li><button type="button" class="result" data-info-pin=${p.id} @click=${() => void reveal(p)}>
          <b>${p.sender.id === me?.id ? say.common.you : p.sender.displayName}</b><small>${listTime(p.createdAt, deps.now())}</small>
          <span>${clip(p.body, CLIP.pin)}</span></button></li>`)}</ul>`}
      ${media(c.id)}
      ${c.kind !== 'group' ? nothing : html`
        <h3>${say.chat.membersHeading}</h3>
        <ul class="info-list">${(people.get(c.id) ?? []).map((m) => {
          const inside = html`${avatar(m.displayName, m.avatar, 'sm')}
            <span>${m.displayName}${m.isMe ? say.chat.youSuffix : ''}</span>${m.role === 'leader' ? html`<span class="tag">${say.common.leader}</span>` : m.role === 'deputy' ? html`<span class="tag">${say.common.deputy}</span>` : nothing}`;
          return html`<li data-info-member=${m.id}>${m.isMe ? html`<div class="info-person">${inside}</div>`
            : html`<button class="info-person" type="button" data-open-card=${m.id} @click=${() => openCard(m.id)}>${inside}</button>`}</li>`;
        })}</ul>
        <div class="info-actions">
          <a class="btn secondary small" href="/groups/${c.id}#add" data-info-add>${icons.users()} ${say.chat.addMembers}</a>
          <a class="btn secondary small" href="/groups/${c.id}">${say.chat.manageGroup}</a>
          ${leaveView(c)}
        </div>`}
      ${page ? infoFoot(c) : nothing}
    </aside>`;

  /** In a one-to-one chat either person may set it (the server checks they are still friends); in a group, the leader and the deputies. */
  const maySetDisappear = (c: Conversation): boolean => c.kind === 'direct'
    ? c.peer !== null && c.peer.deleted !== true
    : (people.get(c.id) ?? []).some((m) => m.isMe && (m.role === 'leader' || m.role === 'deputy'));

  /** Disappearing messages, in the information panel: a choice for those who may set it, else what it is. */
  const disappearRow = (c: Conversation) => {
    const now = c.disappearSeconds ?? null;
    const name = (s: number | null) => s === null ? say.chat.disappearOffChoice : say.chat.lasts(s);
    return html`
      <div class="info-setting" data-info-disappear>
        <label for="disappear">${icons.clock()} ${say.chat.disappear}</label>
        ${maySetDisappear(c) ? html`<select id="disappear" .value=${liveValue(String(now ?? ''))} @change=${(e: Event) => void setDisappear(c, (e.target as HTMLSelectElement).value)}>
            ${[null, ...limits().disappearChoices].map((s) => html`<option value=${s ?? ''} ?selected=${s === now}>${name(s)}</option>`)}</select>`
          : html`<b>${name(now)}</b>`}
      </div>
      <p class="hint">${say.chat.disappearNote}</p>`;
  };

  async function setDisappear(c: Conversation, value: string): Promise<void> {
    const seconds = value === '' ? null : Number(value);
    const before = c.disappearSeconds ?? null;
    c.disappearSeconds = seconds;   // the line in the conversation and the list follow from the server
    update();
    const result = await api.setDisappear(c.id, seconds);
    if (!result.ok) { c.disappearSeconds = before; notice = errorText(result); update(); }
  }

  /** A phone: the information as a page over the thread, with a way back. */
  const infoSheet = (c: Conversation, t: Thread) => html`
    <div class="info-sheet" data-info-page>
      <header class="th-head"><button class="ib back" type="button" aria-label=${say.common.back} data-info-page-close @click=${backFromInfoPage}>${icons.back()}</button>
        <b>${say.chat.infoTitle}</b></header>
      ${infoPanel(c, t, true)}
    </div>`;

  /** At the foot of the phone's page: hide, block (one to one) and clear, as the list's menu has them. */
  const infoFoot = (c: Conversation) => html`
    <div class="info-foot">
      ${c.hidden ? nothing : html`<button class="btn secondary" type="button" data-info-hide @click=${() => { closeInfoPage(); void hideConversation(c); }}>${icons.eyeOff()} ${say.chat.hide}</button>`}
      ${c.kind === 'group' || c.peer === null || c.peer.deleted ? nothing
        : html`<button class="btn secondary" type="button" data-info-block @click=${() => { openCard(c.peer!.id); card!.asking = 'block'; update(); }}>${icons.block()} ${say.common.block}</button>`}
      ${infoClearing ? html`
        <div class="ask" role="group" aria-labelledby="clear-q" data-info-clear-ask>
          <p id="clear-q">${say.chat.clearQ}</p>
          <button class="btn small" type="button" data-info-clear-yes @click=${() => { closeInfoPage(); void clearConversation(c); }}>${say.chat.deleteForMe}</button>
          <button class="btn secondary small" type="button" data-info-clear-no @click=${() => { infoClearing = false; update(); }}>${say.common.cancel}</button>
        </div>` : html`<button class="btn secondary danger-text" type="button" data-info-clear @click=${() => { infoClearing = true; update(); }}>${icons.trash()} ${say.chat.clear}</button>`}
    </div>`;

  /** Under a 1:1 conversation's name: their Gom ID only (H: the groups you share are in the info panel, so the line never wraps on a phone). */
  function peerLine(c: Conversation): string {
    if (c.peer?.deleted) return say.chat.accountDeletedShort;
    return c.peer?.gomId ? `@${c.peer.gomId}` : '';
  }

  /**
   * Leaving the group from the side panel, after a question. The leader of a group with others in it hands over first (on the
   * group's page); a leader alone in it ends the group by leaving, and the question says so.
   */
  function leaveView(c: Conversation): TemplateResult {
    const members = people.get(c.id);
    const mine = members?.find((m) => m.isMe);
    if (mine?.role === 'leader' && (members?.length ?? 0) > 1) return html`
      <button class="btn secondary small" type="button" disabled data-info-leave>${say.common.leaveGroup}</button>
      <p class="hint" data-info-leader-note>${say.chat.leaderMustHandOver}</p>`;
    if (!leaving) return html`<button class="btn secondary small danger-text" type="button" data-info-leave @click=${() => { leaving = true; update(); }}>${say.common.leaveGroup}</button>`;
    return html`
      <div class="ask" role="group" aria-labelledby="leave-q" data-info-leave-ask>
        <p id="leave-q"><b>${say.chat.leaveQ}</b>${mine?.role === 'leader' ? html`<br><span class="hint">${say.chat.lastOneDeletes}</span>` : nothing}</p>
        <button class="btn small" type="button" data-info-leave-yes @click=${() => void leaveGroup(c.id)}>${say.common.leaveGroup}</button>
        <button class="btn secondary small" type="button" data-info-leave-no @click=${() => { leaving = false; update(); }}>${say.chat.stay}</button>
      </div>`;
  }

  /** Someone's card: who they are to you, what you can do, and the groups you are both in. */
  /**
   * A tap or click outside an open dialog (on its shade) closes it, as the X does. The shade's clicks reach the dialog itself, and so do
   * those on its own padding, so where the pointer was decides. (`closedby="any"` does this in Chrome 134+, not yet in Safari.)
   */
  const outside = (close: () => void) => (e: MouseEvent) => {
    const d = e.currentTarget as HTMLElement;
    if (e.target !== d) return;
    const r = d.getBoundingClientRect();
    if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) close();
  };

  const cardView = () => {
    if (card === null) return nothing;
    const k = card;
    const p = k.data;
    const name = p?.displayName ?? '';
    // A friend request to someone who is not a friend goes through a group you are both in (an id alone is no way to find people).
    const via = p?.commonGroups[0]?.id ?? null;
    const close = () => { card = null; update(); };
    return html`
      <dialog class="pcard" aria-labelledby="pcard-name" data-person-card data-modal @close=${close} @click=${outside(close)}
        @keydown=${(e: KeyboardEvent) => { if (e.key === 'Escape') { e.preventDefault(); close(); } }}>
        <div class="pcard-head"><button class="ib" type="button" data-close-card aria-label=${say.common.close} @click=${close}>${icons.close()}</button></div>
        ${p === null ? (k.error === null ? html`<p class="hint" role="status">${say.common.loading}</p>` : errorBox(k.error)) : html`
          <div class="pcard-top">
            ${avatar(name, p.avatar, 'xl')}
            <h2 id="pcard-name">${name}</h2>
            ${p.gomId === null ? nothing : html`<small>@${p.gomId}</small>`}
          </div>
          ${k.error === null ? nothing : errorBox(k.error)}
          ${p.deleted ? html`<p class="hint" data-card-deleted>${say.common.accountDeleted}</p>` : k.asking !== null ? html`
            <div class="ask" role="group" aria-labelledby="card-q" data-card-ask>
              <p id="card-q"><b>${k.asking === 'unfriend' ? say.common.unfriendQ(name) : say.common.blockQ(name)}</b><br>
                <span class="hint">${k.asking === 'unfriend' ? say.common.unfriendNote
                  : say.common.blockNote(name)}</span></p>
              <button class="btn small" type="button" data-card-yes ?disabled=${k.busy} @click=${() => void cardAction(k.asking === 'unfriend' ? () => api.unfriend(p.gomId!) : () => api.block(p.gomId!))}>${k.asking === 'unfriend' ? say.common.unfriend : say.common.block}</button>
              <button class="btn secondary small" type="button" data-card-no @click=${() => { k.asking = null; update(); }}>${say.common.no}</button>
            </div>` : html`
            <div class="pcard-actions">
              ${p.friend === 'friends' && p.gomId !== null ? html`<button class="btn small" type="button" data-card-message ?disabled=${k.busy} @click=${() => void messageFrom(p.gomId!)}>${say.chat.message}</button>` : nothing}
              ${p.friend === 'sent' ? html`<span class="tag" data-card-sent>${say.common.requestSent}</span>` : nothing}
              ${(p.friend === 'none' || p.friend === 'received') && via !== null ? html`<button class="btn small" type="button" data-card-befriend ?disabled=${k.busy}
                @click=${() => void cardAction(() => api.askMember(via, p.id))}>${p.friend === 'received' ? say.common.acceptFriend : say.common.befriend}</button>` : nothing}
              ${p.friend === 'friends' && p.gomId !== null ? html`
                <button class="btn secondary small" type="button" data-card-unfriend @click=${() => { k.asking = 'unfriend'; update(); }}>${say.common.unfriend}</button>
                <button class="btn secondary small danger-text" type="button" data-card-block @click=${() => { k.asking = 'block'; update(); }}>${say.common.block}</button>` : nothing}
            </div>`}
          ${p.commonGroups.length === 0 ? nothing : html`
            <h3>${say.chat.commonGroups(p.commonGroups.length)}</h3>
            <ul class="info-list" data-card-groups>${p.commonGroups.map((g) => html`<li>
              <button class="info-person" type="button" data-card-group=${g.id} @click=${() => { card = null; open(g.id); }}>
                ${avatar(g.title ?? '?')}<span>${g.title ?? '…'}</span><small>${say.common.members(g.memberCount)}</small></button></li>`)}</ul>`}`}
      </dialog>`;
  };

  function threadPane(): TemplateResult {
    const c = chatOf(openId);
    if (openId === null) return html`<section class="thread-pane none"><div class="empty welcome"><div class="big-icon">${icons.logo()}</div><h2>Gom</h2><p class="lead">${say.chat.pickOne}</p></div></section>`;
    if (chats !== null && c === undefined) return html`
      <section class="thread-pane">
        <header class="th-head"><a class="ib back" href="/" aria-label=${say.common.back} @click=${onBack}>${icons.back()}</a></header>
        <div class="empty"><p class="lead">${say.chat.chatNotFound}</p></div>
      </section>`;
    const t = threadOf(openId);
    const items = buildThread(t.messages, me?.id ?? '', t.lastReadAtOpen, t.unreadAtOpen, deps.now(), t.pending);
    // Worked out once a drawing, not once a message: seenText looks through the whole thread.
    const group = c?.kind === 'group', seen = seenText(t, group), tick = tickOf(t, c);
    return html`
      <section class="thread-pane" data-palette=${c?.palette ?? nothing} @dragover=${(e: DragEvent) => { if (e.dataTransfer?.types.includes('Files')) e.preventDefault(); }}
        @drop=${(e: DragEvent) => { if (e.dataTransfer?.files.length) { e.preventDefault(); addFiles(openId!, [...e.dataTransfer.files]); } }}>
        <header class="th-head">
          <a class="ib back" href="/" aria-label=${say.common.back} @click=${onBack}>${icons.back()}</a>
          ${avatar(c?.title ?? '?', c ? pictureOf(c) : null)}
          ${c?.peer ? html`<button class="th-title as-link" type="button" data-peer-card aria-label=${say.chat.seeInfo(c.title ?? '')} @click=${() => openCard(c.peer!.id)}>
              <b>${c.title ?? '…'}</b><small>${peerLine(c)}</small></button>`
            : html`<div class="th-title"><b>${c?.title ?? '…'}</b>${c?.kind === 'group' ? html`<small>${say.common.members(c.memberCount ?? 0)}</small>` : nothing}</div>`}
          ${c?.disappearSeconds ? html`<span class="th-timer" role="img" data-disappear-on aria-label=${say.chat.disappearIn(say.chat.lasts(c.disappearSeconds))}
            title=${say.chat.disappearIn(say.chat.lasts(c.disappearSeconds))}>${icons.clock()}</span>` : nothing}
          ${callable(c) ? html`<button class="ib" type="button" data-call-start aria-label=${say.calls.call} @click=${() => callIn(c)}>${icons.call()}</button>` : nothing}
          ${c === undefined ? nothing : html`<button class="ib wide" type="button" data-search aria-label=${say.chat.searchIn} aria-expanded=${searching ? 'true' : 'false'}
            @click=${() => { searching = searching ? null : { q: '', results: null, asked: '', ticket: 0 }; update(); if (searching) root.querySelector<HTMLInputElement>('.searchbar input')?.focus(); }}>${icons.search()}</button>
          <button class="ib wide" type="button" data-mute aria-pressed=${isMuted(c) ? 'true' : 'false'}
            aria-label=${isMuted(c) ? say.chat.unmuteAgain : say.common.mute} @click=${() => onBell(c)}>${isMuted(c) ? icons.bellOff() : icons.bell()}</button>`}
          ${c?.kind === 'group' ? html`<a class="ib wide" href="/groups/${c.id}" aria-label=${say.chat.groupInfo}>${icons.users()}</a>` : nothing}
          ${c === undefined ? nothing : html`<button class="ib info-btn" type="button" data-info aria-label=${say.chat.info} aria-pressed=${infoOpen ? 'true' : 'false'} @click=${toggleInfo}>${icons.info()}</button>
            <button class="ib info-page-btn" type="button" data-info-page-open aria-label=${say.chat.info} @click=${openInfoPage}>${icons.info()}</button>`}
        </header>
        ${banner()}
        ${searching === null ? nothing : searchPanel(searching)}
        ${pinBar(t)}
        <div class="backdrop" data-pattern=${c?.pattern ?? storedPattern()}>
        <div class="msgs" @scroll=${(e: Event) => onScroll(openId!, e.target as HTMLElement)}>
          ${mentionAt.has(c?.id ?? '') ? html`<button class="tomention" type="button" data-to-mention aria-label=${say.chat.toMention}
            @click=${() => { const at = mentionAt.get(openId!); mentionAt.delete(openId!); if (at !== undefined) void reveal({ id: at.id, conversationId: openId! }); }}>@</button>` : nothing}
          ${t.hasMore ? html`<button class="btn secondary small more" type="button" data-older ?disabled=${t.loading} @click=${() => void loadOlder(openId!)}>${say.chat.older}</button>` : nothing}
          ${repeat(items, (i) => i.key, (i) => threadItem(i, group, seen, tick))}
        </div>
        ${copyNote === null ? nothing : html`<div class="copy-note" role="status" data-copy-note>${copyNote.text}</div>`}
        </div>
        ${menuOverlay(t)}
        ${viewerView()}
        ${t.stick ? nothing : html`<button class="toend" type="button" data-to-end aria-label=${say.chat.toLatest} @click=${() => { t.below = 0; t.stick = true; update(); }}>${icons.down()}</button>`}
        ${t.below > 0 ? html`<button class="jump" type="button" data-jump-bottom @click=${() => { t.below = 0; t.stick = true; update(); }}>${icons.down()}<span>${say.chat.newBelow(t.below)}</span></button>` : nothing}
        ${forwarding === null ? nothing : forwardPanel(forwarding)}
        ${cardView()}
        ${c === undefined ? nothing : typingLine(c.id, c.kind)}
        ${mentionView()}
        ${c?.peer?.deleted ? html`
          <p class="closed-note" role="status" data-peer-deleted>${say.chat.peerDeletedNote}</p>` : html`
          ${t.editing === null ? nothing : html`
            <div class="replybar" data-editbar>
              <div class="rb-text"><b>${say.chat.editing}</b></div>
              <button class="ib" type="button" data-cancel-edit aria-label=${say.chat.cancelEdit} @click=${() => { t.editing = null; t.draft = ''; update(); }}>${icons.close()}</button>
            </div>`}
          ${t.replying === null ? nothing : html`
            <div class="replybar" data-replybar>
              <div class="rb-text"><b>${say.chat.replyingTo(t.replying.sender?.id === me?.id ? say.chat.yourself : t.replying.sender?.displayName ?? '')}</b><span>${t.replying.sticker ? say.common.sticker : quoted(t.replying)}</span></div>
              <button class="ib" type="button" data-cancel-reply aria-label=${say.chat.cancelReply} @click=${() => { t.replying = null; update(); }}>${icons.close()}</button>
            </div>`}
          ${trayView(t)}
          ${recording?.conversation === openId ? recordingBar(recording) : html`
          <form class="composer" @submit=${(e: Event) => { e.preventDefault(); void send(openId!); }}>
            ${t.editing === null && uploadsOn() ? html`
              <button class="ib attach" type="button" data-gallery aria-label=${say.chat.picturesAndVideos} @click=${() => root.querySelector<HTMLInputElement>('input[name="gallery"]')?.click()}>${icons.image()}</button>
              <input class="file-input" type="file" name="gallery" accept="image/*,video/*" multiple tabindex="-1" aria-hidden="true"
                @change=${(e: Event) => { const input = e.target as HTMLInputElement; addFiles(openId!, [...(input.files ?? [])]); input.value = ''; }}>
              <button class="ib attach" type="button" data-attach aria-label=${say.chat.attach} @click=${() => root.querySelector<HTMLInputElement>('input[name="files"]')?.click()}>${icons.attach()}</button>
              <input class="file-input" type="file" name="files" multiple tabindex="-1" aria-hidden="true"
                @change=${(e: Event) => { const input = e.target as HTMLInputElement; addFiles(openId!, [...(input.files ?? [])]); input.value = ''; }}>` : nothing}
            <div class="field">
            <textarea name="message" rows="1" data-kept aria-label=${say.chat.typeMessage} placeholder=${say.chat.typeMessage} autocomplete="off" enterkeyhint="enter"
              .value=${liveValue(t.draft)} @input=${onDraft} @keydown=${onKey} @click=${(e: Event) => watchMention(e.target as HTMLTextAreaElement)} @keyup=${(e: KeyboardEvent) => { if (e.key.startsWith('Arrow') || e.key === 'Home' || e.key === 'End') watchMention(e.target as HTMLTextAreaElement); }} @paste=${(e: ClipboardEvent) => onPaste(e)}
              @focus=${() => { if (picker !== null) { picker = null; update(); } }}></textarea>
            ${t.editing === null ? html`<button class="ib picker-btn" type="button" data-picker-toggle aria-label=${say.chat.picker} aria-expanded=${picker !== null ? 'true' : 'false'}
              @click=${togglePicker}>${icons.smile()}</button>` : nothing}
            </div>
            ${voiceOffered(t) ? html`<button class="send" type="button" data-voice aria-label=${say.chat.recordVoice} @click=${() => void startVoice(openId!)}>${icons.mic()}</button>`
              : html`<button class="send" type="submit" aria-label=${say.chat.send} ?disabled=${t.tray.some((i) => i.state !== 'done')}>${icons.send()}</button>`}
          </form>`}
          ${picker !== null && t.editing === null ? pickerPanel(openId!, picker) : nothing}`}
      </section>`;
  }

  /** The panel under the composer, where the keyboard would be: a tab of emoji and a tab of stickers. */
  const pickerPanel = (id: string, tab: 'emoji' | 'stickers') => html`
    <div class="picker" data-picker
      @keydown=${(e: KeyboardEvent) => { if (e.key === 'Escape') { picker = null; update(); root.querySelector<HTMLButtonElement>('[data-picker-toggle]')?.focus(); } }}>
      <div class="picker-tabs" role="tablist" aria-label=${say.chat.picker}>
        <button class="ptab" type="button" role="tab" data-tab="emoji" aria-selected=${tab === 'emoji' ? 'true' : 'false'} @click=${() => showTab('emoji')}>Emoji</button>
        <button class="ptab" type="button" role="tab" data-tab="stickers" aria-selected=${tab === 'stickers' ? 'true' : 'false'} @click=${() => showTab('stickers')}>${say.common.sticker}</button>
      </div>
      ${tab === 'emoji' ? emojiPanel(id) : stickerPanel(id)}
    </div>`;

  /** The emoji, a group at a time (the ones used lately first, when there are some). A tap puts one where the cursor was. */
  const emojiPanel = (id: string) => {
    const recent = recentEmoji();
    const group = emojiGroup === -1 && recent.length === 0 ? 0 : emojiGroup;
    const shown = group === -1 ? recent : emojiOf(group);
    const chooser = (index: number, face: string, name: string) => html`
      <button class="egroup" type="button" data-emoji-group=${index} aria-label=${name} aria-pressed=${group === index ? 'true' : 'false'}
        @click=${() => { emojiGroup = index; update(); }}>${face}</button>`;
    return html`
      <section class="emojis" role="tabpanel" aria-label="Emoji" data-emoji-panel>
        <div class="egroups">${recent.length > 0 ? chooser(-1, '🕘', say.common.recent) : nothing}${EMOJI_GROUPS.map((g, i) => chooser(i, emojiOf(i)[0] ?? '', say.emojiGroups[g.key]))}</div>
        <h3>${group === -1 ? say.common.recent : say.emojiGroups[EMOJI_GROUPS[group]!.key]}</h3>
        <div class="e-grid">${shown.map((e) => html`<button class="e-pick" type="button" data-emoji=${e} aria-label=${e} @click=${() => insertEmoji(id, e)}>${e}</button>`)}</div>
      </section>`;
  };

  /**
   * The stickers, a pack at a time, as the emoji are shown a group at a time; "Gần đây" (sent lately from this device) comes first once
   * there are some. Only the pack shown is fetched. A tap sends one at once, as Zalo does.
   */
  const stickerPanel = (id: string) => {
    const recent = recentStickers();
    // Gom's own packs, then the owner's.
    const packs: { name: string; codes: readonly string[] }[] = [
      ...STICKER_PACKS.map((p) => ({ name: say.stickerPacks[p.key], codes: p.codes })),
      ...(packsFor === openId ? customPacks ?? [] : []).map((p) => ({ name: p.name, codes: p.stickers.map((s) => s.code) })),
    ];
    const pack = stickerPack === -1 && recent.length === 0 ? 0 : stickerPack;
    const shown = pack === -1 ? recent : packs[pack]?.codes ?? [];
    const tile = (code: string) => html`<button class="stk-pick" type="button" data-send-sticker=${code} aria-label=${say.chat.sendSticker(stickerName(code))}
      @click=${() => void sendSticker(id, code)}><img src=${stickerUrl(code)} alt="" loading="lazy" width="56" height="56" draggable="false"></button>`;
    const chooser = (index: number, face: TemplateResult | string, name: string) => html`
      <button class="egroup" type="button" data-sticker-pack=${index} aria-label=${name} aria-pressed=${pack === index ? 'true' : 'false'}
        @click=${() => { stickerPack = index; update(); }}>${face}</button>`;
    return html`
      <section class="stickers" role="tabpanel" aria-label=${say.common.sticker} data-sticker-panel>
        <div class="egroups">${recent.length > 0 ? chooser(-1, '🕘', say.common.recent) : nothing}${packs.map((p, i) =>
          chooser(i, html`<img src=${stickerUrl(p.codes[0]!)} alt="" width="26" height="26" draggable="false">`, p.name))}</div>
        <h3>${pack === -1 ? say.common.recent : packs[pack]?.name ?? ''}</h3>
        <div class="stk-grid" data-shown-stickers>${shown.map(tile)}</div>
        <p class="credit">${say.chat.stickerCredit}</p>
      </section>`;
  };

  const PICKER_TAB = 'gom-picker-tab';

  /** Opens or closes the panel, on the tab used last. Opening it puts the keyboard away on a phone, so the panel takes its place. */
  function togglePicker(): void {
    if (picker !== null) { picker = null; update(); return; }
    picker = ((): 'emoji' | 'stickers' => { try { return localStorage.getItem(PICKER_TAB) === 'emoji' ? 'emoji' : 'stickers'; } catch { return 'stickers'; } })();
    // Opens on "Gần đây" when there are some, else on the first pack; it then stays where the person put it, sending or not.
    stickerPack = recentStickers().length > 0 ? -1 : 0;
    if (picker === 'stickers') loadCustomPacks();
    root.querySelector<HTMLTextAreaElement>('.composer textarea')?.blur();
    update();
  }

  function showTab(tab: 'emoji' | 'stickers'): void {
    picker = tab;
    if (tab === 'stickers') loadCustomPacks();
    try { localStorage.setItem(PICKER_TAB, tab); } catch { /* just not remembered */ }
    update();
  }

  /** Puts an emoji in the message field where the cursor was (it stays where it is after the field loses focus), without opening the keyboard. */
  function insertEmoji(id: string, emoji: string): void {
    const field = root.querySelector<HTMLTextAreaElement>('.composer textarea');
    if (field === null) return;
    field.setRangeText(emoji, field.selectionStart, field.selectionEnd, 'end');
    setDraft(id, field.value);
    rememberEmoji(emoji);
    update();
  }

  /**
   * The list, or with something typed above it, what it finds by name: friends (their conversations, then friends not written to
   * yet), then groups, then hidden conversations under their own heading. Messages are searched inside a conversation only.
   */
  const listView = () => {
    if (filter.trim() === '') return html`<div class="items"><ul class="rows">${repeat(shownChats(), (c) => c.id, row)}</ul></div>`;
    const shown = visibleChats();
    const directs = shown.filter((c) => c.kind === 'direct');
    const groups = shown.filter((c) => c.kind === 'group');
    const talkedTo = new Set((chats ?? []).map((c) => c.peer?.gomId).filter((g) => g !== undefined));
    const others = (friends ?? []).filter((f) => !talkedTo.has(f.gomId) && matches(`${f.displayName} ${f.gomId}`, filter));
    const found = hiddenMatches(filter);
    if (directs.length === 0 && others.length === 0 && groups.length === 0 && found.length === 0) return html`<p class="hint nomatch" data-no-match>${say.chat.noChatsFound}</p>`;
    // One scroller for all, so each part follows the one before instead of splitting the pane.
    return html`<div class="items">
      ${directs.length === 0 && others.length === 0 ? nothing : html`<h3 class="hidden-label" data-found-friends>${say.chat.foundFriends}</h3>
        <ul class="rows">${repeat(directs, (c) => c.id, row)}${repeat(others, (f) => f.gomId, friendRow)}</ul>`}
      ${groups.length === 0 ? nothing : html`<h3 class="hidden-label" data-found-groups>${say.chat.foundGroups}</h3><ul class="rows">${repeat(groups, (c) => c.id, row)}</ul>`}
      ${found.length === 0 ? nothing : html`${hiddenLabel()}<ul class="rows" data-hidden-list>${repeat(found, (c) => c.id, row)}</ul>`}</div>`;
  };

  /** A friend the search found who has no conversation with you yet: a tap makes it and opens it. */
  const friendRow = (f: Person) => html`<li class="row"><button type="button" class="item" data-start-chat=${f.gomId} @click=${() => void chatWith(f.gomId)}>
      ${avatar(f.displayName, f.avatar)}
      <div class="im"><div class="l1"><span class="nm">${f.displayName}</span></div><div class="l2"><span class="pv">@${f.gomId}</span></div></div></button></li>`;

  const view = () => html`
    <div class="chat" data-pane=${openId === null ? 'list' : 'thread'} data-infostate=${infoOpen && openId !== null ? 'open' : 'closed'}>
      ${rail()}
      <section class="list-pane">
        <header class="bar">
          <a class="brand" href="/" aria-label=${say.chat.home} @click=${onHome}>${icons.logo()}<span>Gom</span></a>
          <a class="ib" href="/friends" aria-label=${say.common.contacts}>${icons.users()}</a>
          <a class="ib" href="/settings" aria-label=${say.common.settings}>${icons.gear()}</a>
        </header>
        ${chats === null || chats.length === 0 ? nothing : html`
          <div class="searchfield input list-search">${icons.search()}
            <input type="search" name="filter" aria-label=${say.common.searchChats} placeholder=${say.common.search} autocomplete="off" maxlength=${limits().nameChars}
              .value=${liveValue(filter)} @input=${(e: Event) => { filter = (e.target as HTMLInputElement).value; update(); loadFriends(); }}
              @keydown=${(e: KeyboardEvent) => { if (e.key === 'Escape') { filter = ''; update(); } }}></div>`}
        ${banner()}
        ${pushBanner()}
        ${errorBox(error)}
        ${notice === '' ? nothing : html`<p class="note" role="status">${notice}</p>`}
        ${me === null || chats === null ? nothing : shownChats().length === 0 && filter === '' ? empty() : listView()}
      </section>
      ${threadPane()}
      ${openId !== null && infoOpen && chatOf(openId) !== undefined ? infoPanel(chatOf(openId)!, threadOf(openId)) : nothing}
      ${openId !== null && infoPage && chatOf(openId) !== undefined ? infoSheet(chatOf(openId)!, threadOf(openId)) : nothing}
      ${rowMenuView()}
      ${muteView()}
      ${appFileView()}
      ${lookView()}
      ${undoView()}
    </div>`;

  /** Once per device: turn notifications on from here (the browser asks in the tap itself), or not now. */
  const pushBanner = () => pushOffer === null ? nothing : html`
    <div class="pushbar" data-push-banner>
      ${icons.bell()}
      <span>${pushOffer.noService ? NO_PUSH_SERVICE : pushOffer.failed ? say.chat.pushFailed : say.chat.pushOffer}</span>
      ${pushOffer.failed ? nothing : html`<button class="btn small" type="button" data-push-yes ?disabled=${pushOffer.busy} @click=${() => void acceptPush()}>${say.chat.turnOn}</button>`}
      <button class="btn secondary small" type="button" data-push-later ?disabled=${pushOffer.busy} @click=${laterPush}>${pushOffer.failed ? say.common.close : say.chat.later}</button>
    </div>`;

  /** Offered only where it can work: push on at the server, this device not registered, the browser able and not blocking Gom. */
  async function offerPush(): Promise<void> {
    const p = deps.pusher;
    if (p === undefined || p.support() !== 'ok' || p.permission() === 'denied') return;
    try { if (localStorage.getItem(PUSH_LATER) === '1') return; } catch { /* no storage: offer it */ }
    const state = await api.pushState();
    if (!state.ok || state.data.subscribed) return;
    pushOffer = { key: state.data.key, level: state.data.level, busy: false, failed: false };
    update();
  }

  async function acceptPush(): Promise<void> {
    const p = deps.pusher;
    if (pushOffer === null || p === undefined || pushOffer.busy) return;
    pushOffer.busy = true; update();
    const sub = await p.subscribe(pushOffer.key);
    const result = sub === 'denied' || sub === 'failed' || sub === 'no-service' ? null : await api.setPush(sub, pushOffer.level);
    if (sub === 'denied' || result?.ok) pushOffer = null;   // refused: the browser will not ask again, and Settings explains
    else if (sub === 'no-service') {
      // It never can on this browser: said once, and not offered again here.
      pushOffer = { ...pushOffer, busy: false, failed: true, noService: true };
      try { localStorage.setItem(PUSH_LATER, '1'); } catch { /* it is just not remembered */ }
    } else pushOffer = { ...pushOffer, busy: false, failed: true };
    update();
  }

  function laterPush(): void {
    pushOffer = null;
    try { localStorage.setItem(PUSH_LATER, '1'); } catch { /* it is just not remembered */ }
    update();
  }

  /**
   * Scrolls the message list, and only it, so that `el` is at its top or in its middle (as far as the list can go). Not
   * scrollIntoView: when the list cannot scroll that far (the first unread message near the end), that also scrolls the page
   * around it, and the header goes off the top of the screen.
   */
  function scrollWithin(box: HTMLElement, el: HTMLElement, where: 'start' | 'center'): void {
    const offset = el.getBoundingClientRect().top - box.getBoundingClientRect().top + box.scrollTop;
    box.scrollTop = Math.max(0, where === 'start' ? offset : offset - (box.clientHeight - el.offsetHeight) / 2);
  }

  function update(): void {
    dropExpired();
    render(view(), root);
    scheduleExpiry();
    placeMenu();
    showDialogs();
    const after = root.querySelector<HTMLElement>('.msgs');
    if (after === null || openId === null) return;
    const t = threadOf(openId);
    if (scrollAnchor !== null) { after.scrollTop += after.scrollHeight - scrollAnchor; scrollAnchor = null; return; }
    if (t.jump) {
      // The first unread message is where reading starts. It can be drawn only once the history is here.
      const divider = after.querySelector<HTMLElement>('.newdiv');
      if (divider !== null) { t.jump = false; t.stick = false; scrollWithin(after, divider, 'start'); return; }
      if (t.loaded) t.jump = false;   // the first unread one is further back than what was loaded: read from the end
    }
    if (t.stick) after.scrollTop = after.scrollHeight;
  }

  /**
   * A dialog drawn for the first time is opened as a modal one (the rest of the page cannot be reached, and Escape or the Back gesture
   * closes it). Then the viewer's strip is moved to the current picture, if asked for.
   */
  function showDialogs(): void {
    for (const d of root.querySelectorAll<HTMLDialogElement>('dialog[data-modal]')) {
      if (d.open) continue;
      if (typeof d.showModal === 'function') d.showModal(); else d.setAttribute('open', '');
    }
    const track = root.querySelector<HTMLElement>('[data-viewer-track]');
    if (viewer === null || !viewerSync || track === null) return;
    // The strip of small pictures (a computer's) keeps the current one in view.
    root.querySelector<HTMLElement>('[data-viewer-thumbs] [aria-current="true"]')?.scrollIntoView?.({ block: 'nearest', inline: 'center' });
    if (track.clientWidth <= 0) return;
    const i = slidesOf(viewer).findIndex((s) => s.attachment.id === viewer!.attachmentId);
    const left = Math.max(0, i) * track.clientWidth;
    track.scrollLeft = left;
    viewerSync = false;
  }

  // ---- the person's actions

  function onRowClick(e: MouseEvent, id: string): void {
    // The click that ends a long press opened the menu; it does not also open the conversation.
    if (rowPressOpened) { rowPressOpened = false; e.preventDefault(); return; }
    if (e.defaultPrevented || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
    e.preventDefault();
    open(id);
  }

  function onBack(e: MouseEvent): void {
    if (e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
    e.preventDefault();
    deps.nav.push('/');
    toList();
  }

  /** Back to the list: on a phone the conversation slides out first (motion.ts), as it slid in. */
  function toList(): void {
    cancelVoice();
    const leaving = openId;
    leaveThread(root.querySelector('.thread-pane'), () => { if (openId === leaving) { openId = null; update(); } });
  }

  /** The Gom button: back to the start of the app (the list, nothing open). Already there, it does nothing. */
  function onHome(e: MouseEvent): void {
    if (openId === null) { if (!(e.ctrlKey || e.metaKey || e.shiftKey || e.altKey)) e.preventDefault(); return; }
    onBack(e);
  }

  function onScroll(id: string, box: HTMLElement): void {
    const t = threadOf(id);
    const wasStuck = t.stick;
    const hadNew = t.below;
    t.stick = box.scrollHeight - box.scrollTop - box.clientHeight < GAP_NEAR_BOTTOM_PX;
    if (t.stick) t.below = 0;
    // The arrow to a mention goes once that message has come into view.
    const at = mentionAt.get(id);
    const target = at === undefined ? null : box.querySelector<HTMLElement>(`[data-msg="${at.id}"]`);
    if (target !== null) {
      const [row, view] = [target.getBoundingClientRect(), box.getBoundingClientRect()];
      if (row.bottom > view.top && row.top < view.bottom) { mentionAt.delete(id); update(); }
    }
    // The arrow to the end shows while you are away from it: the page is drawn again when that changes, not on every scroll.
    if (wasStuck !== t.stick || hadNew !== t.below) update();
  }

  function onDraft(e: Event): void {
    const field = e.target as HTMLTextAreaElement;
    if (openId === null) return;
    const wasEmpty = threadOf(openId).draft.trim() === '';
    setDraft(openId, field.value);
    if (field.value.trim() !== '' && threadOf(openId).editing === null) sayTyping(openId);
    watchMention(field);
    if (wasEmpty !== (field.value.trim() === '') && deps.recordVoice !== undefined) update();   // the send button and the microphone trade places
  }

  // ---- mentions: the "@" list in the message field

  /** What the list offers: "@all" first (anyone in the group may use it), then the other members, narrowed by what is typed (no accents needed). */
  function mentionChoices(): Array<{ kind: 'all' } | { kind: 'member'; id: string; name: string; conversation: string }> {
    if (mention === null) return [];
    const q = mention.query;
    const members = (people.get(mention.conversation) ?? []).filter((p) => !p.isMe && matches(p.displayName, q));
    return [...(matches(`all ${say.chat.mentionAll}`, q) ? [{ kind: 'all' as const }] : []),
      ...members.slice(0, 8).map((p) => ({ kind: 'member' as const, id: p.id, name: p.displayName, conversation: mention!.conversation }))];
  }

  /** Opens the list when the caret is just after an "@" that begins a word (at the start, or after a space or punctuation), in a group; closes it otherwise. */
  function watchMention(field: HTMLTextAreaElement): void {
    const id = openId;
    if (id === null || chatOf(id)?.kind !== 'group') { if (mention !== null) { mention = null; update(); } return; }
    const found = /(?:^|[\s(\[{"'.,;:!?])@([^\s@]{0,30})$/u.exec(field.value.slice(0, field.selectionStart));
    if (found === null) { if (mention !== null) { mention = null; update(); } return; }
    const at = field.selectionStart - found[1]!.length - 1;
    void loadPeople(id);
    mention = { conversation: id, at, query: found[1]!, sel: mention?.at === at ? mention.sel : 0 };
    update();
  }

  /** Puts the choice in the words where the "@" was: "@" and the name (the server keeps it as a mention only while it is written so). */
  function pickMention(choice: { kind: 'all' } | { kind: 'member'; id: string; name: string }): void {
    if (mention === null) return;
    const { conversation, at } = mention;
    const field = root.querySelector<HTMLTextAreaElement>('.composer textarea');
    if (field === null) return;
    const written = choice.kind === 'all' ? '@all' : `@${choice.name}`;
    const value = `${field.value.slice(0, at)}${written} ${field.value.slice(field.selectionStart)}`;
    if (choice.kind === 'member') {
      let chosen = picked.get(conversation);
      if (chosen === undefined) picked.set(conversation, chosen = new Map());
      chosen.set(choice.id, choice.name);
    }
    setDraft(conversation, value);
    mention = null;
    update();
    const caret = at + written.length + 1;
    field.focus();
    field.setSelectionRange(caret, caret);
  }

  /** The members to send as mentioned: those chosen whose name is still written in the words. Forgets the choices. */
  function mentionIds(id: string, body: string): string[] {
    const chosen = picked.get(id);
    picked.delete(id);
    if (chosen === undefined) return [];
    const low = body.toLowerCase();
    return [...chosen].filter(([, name]) => low.includes(`@${name.toLowerCase()}`)).map(([who]) => who);
  }

  const mentionView = (): TemplateResult | typeof nothing => {
    const choices = mentionChoices();
    if (mention === null || mention.conversation !== openId || choices.length === 0) return nothing;
    const lit = Math.min(mention.sel, choices.length - 1);
    return html`<ul class="mention-pop" role="listbox" aria-label=${say.chat.mentionList} data-mention-list>${choices.map((ch, i) => html`
      <li role="option" aria-selected=${i === lit ? 'true' : 'false'}><button type="button" class="mi-pick ${i === lit ? 'sel' : ''}" data-mention-pick=${ch.kind === 'all' ? 'all' : ch.id}
        @pointerdown=${(e: Event) => e.preventDefault()} @click=${() => pickMention(ch)}>${ch.kind === 'all'
          ? html`<span class="av sm all" aria-hidden="true">@</span><span>${say.chat.mentionAll} <small>· @all</small></span>`
          : html`${avatar(ch.name, memberPicture(ch.conversation, ch.id), 'sm')}<span>${ch.name}</span>`}</button></li>`)}</ul>`;
  };

  /** Tells the others that you are typing, at most every `TYPING_REPEAT_MS`. Not when you have turned read receipts off (it is the same switch). */
  function sayTyping(id: string): void {
    if (me?.readReceipts === false) return;
    const now = deps.now().getTime();
    const last = typedAt.get(id);
    if (last !== undefined && now - last < TYPING_REPEAT_MS) return;
    typedAt.set(id, now);
    live?.typing(id);
  }

  /** Someone else is typing here: shown until `TYPING_SHOWN_MS` pass with no further sign, or their message arrives. */
  function showTyping(conversation: string, userId: string, name: string): void {
    if (userId === me?.id || me?.readReceipts === false) return;
    let here = typers.get(conversation);
    if (here === undefined) typers.set(conversation, here = new Map());
    const before = here.get(userId);
    if (before !== undefined) deps.timer.clear(before.timer);
    here.set(userId, { name, timer: deps.timer.set(() => stopTyping(conversation, userId), TYPING_SHOWN_MS) });   // an old key keeps its place
    if (conversation === openId) update();
  }

  function stopTyping(conversation: string, userId: string): void {
    const here = typers.get(conversation);
    const who = here?.get(userId);
    if (here === undefined || who === undefined) return;
    deps.timer.clear(who.timer);
    here.delete(userId);
    if (conversation === openId) update();
  }

  /** The line above the message field. Its height is always there (nothing moves when it fills); a one-to-one chat leaves out the name. */
  const typingLine = (id: string, kind: Conversation['kind']): TemplateResult | typeof nothing => {
    if (me?.readReceipts === false) return nothing;
    const who = [...(typers.get(id)?.values() ?? [])];
    const text = who.length === 0 ? '' : kind === 'direct' ? say.chat.typingDirect
      : say.chat.typing(who.slice(0, TYPING_NAMES).map((w) => w.name).join(', '), Math.min(who.length, TYPING_NAMES), Math.max(0, who.length - TYPING_NAMES));
    return html`<div class="typing" data-typing role="status">${who.length === 0 ? nothing : html`<span class="dots" aria-hidden="true"><i></i><i></i><i></i></span><span>${text}</span>`}</div>`;
  };

  /** The words in the message field, kept for a reload too; not while they are a sent message's being changed. */
  function setDraft(id: string, text: string): void {
    const t = threadOf(id);
    t.draft = text;
    if (t.editing === null) drafts?.save(id, text);
  }

  function onKey(e: KeyboardEvent): void {
    if (mention !== null && !e.isComposing) {
      const choices = mentionChoices();
      if (choices.length > 0 && (e.key === 'ArrowDown' || e.key === 'ArrowUp')) {
        e.preventDefault();
        mention.sel = (Math.min(mention.sel, choices.length - 1) + (e.key === 'ArrowDown' ? 1 : choices.length - 1)) % choices.length;
        update();
        return;
      }
      if (choices.length > 0 && (e.key === 'Enter' || e.key === 'Tab')) { e.preventDefault(); pickMention(choices[Math.min(mention.sel, choices.length - 1)]!); return; }
      if (e.key === 'Escape') { mention = null; update(); return; }
    }
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
    if (recording !== null && recording.conversation !== id) cancelVoice();
    openId = id;
    const t = threadOf(id);
    const c = chatOf(id);
    if (c?.hidden) unhide(c);
    if (c !== undefined) { t.lastReadAtOpen = c.lastReadSeq; t.unreadAtOpen = c.unread; }
    if (c?.mentioned && c.firstMentionId) mentionAt.set(id, { id: c.firstMentionId }); else mentionAt.delete(id);
    t.stick = true;
    t.jump = t.unreadAtOpen > 0;
    t.below = 0;
    leaving = false;
    picker = null;
    update();
    deps.clearNotifications?.(id);
    if (!t.loaded) void loadHistory(id);
    // A 1:1 conversation's info panel lists the groups you share: that comes from the person's card, asked for as it opens.
    if (c?.peer && !cards.has(c.peer.id)) void loadCard(c.peer.id);
    else markRead(id);
    if (infoOpen) { void loadMedia(id, 'image'); void loadMedia(id, 'file'); }
    // A group's members are asked for when it opens: the side panel lists them, and the thread shows their pictures.
    void loadPeople(id);
  }

  // ---- loading

  /** The first page of a conversation being asked for, so something waiting for it (going to a found message) waits for the same one. */
  const historyLoads = new Map<string, Promise<void>>();

  function loadHistory(id: string): Promise<void> {
    let loading = historyLoads.get(id);
    if (loading === undefined) {
      loading = fetchHistory(id).finally(() => historyLoads.delete(id));
      historyLoads.set(id, loading);
    }
    return loading;
  }

  async function fetchHistory(id: string): Promise<void> {
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

  /** The page before the oldest one here; with `to`, everything back to that message at once (a jump: one answer, one drawing). */
  async function loadOlder(id: string, to?: string): Promise<void> {
    const t = threadOf(id);
    const oldest = t.messages[0]?.seq;
    if (t.loading || oldest === undefined) return;
    t.loading = true; update();
    const result = await api.history(id, oldest, to);
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
    // A conversation marked unread is read by opening it, even with nothing new in it.
    if (newest <= c.lastReadSeq && c.unread === 0 && !c.markedUnread) return;
    c.lastReadSeq = Math.max(c.lastReadSeq, newest);
    c.unread = 0;
    c.markedUnread = false;
    c.mentioned = false;
    c.firstMentionSeq = null;
    c.firstMentionId = null;
    update();
    void api.markRead(id, newest);
  }

  /** Takes a created event into the thread and the list. Safe to apply twice: the second time changes nothing. */
  function apply(conversation: string, event: ChatEvent): void {
    const message = event.message;
    const c = chatOf(conversation);
    if (c === undefined) { if (chats !== null) void refreshList(); return; }

    // A message taken back leaves the viewer (if its picture was the one shown, the viewer is gone); the others keep their place.
    if (event.kind === 'deleted' && viewer?.conversation === conversation) viewerSync = true;

    // The side panel's list follows: a new message's pictures and files join it, a message taken back leaves it, changed words follow.
    const lists = mediaLists.get(conversation);
    if (lists !== undefined) {
      if (event.kind === 'deleted') for (const k of ['image', 'file'] as const) lists[k].items = lists[k].items.filter((i) => i.messageId !== message.id);
      else if (event.kind === 'created') for (const a of message.attachments ?? []) lists[shelf(a)].items = mergeMedia(lists[shelf(a)].items, [{ attachment: a, messageId: message.id, seq: message.seq, createdAt: message.createdAt, sender: message.sender, body: message.body }]);
      else for (const k of ['image', 'file'] as const) lists[k].items = lists[k].items.map((i) => i.messageId === message.id ? { ...i, body: message.body } : i);
    }

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
      if (event.kind === 'created' && t.loaded && !t.stick && t.messages.length > had2 && message.sender.id !== me?.id && !message.notice && conversation === openId) t.below++;
      if (event.kind === 'created' && t.loaded && t.messages.length > had2 && message.sender.id !== me?.id && !message.notice && conversation === openId) {
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
        if (c.lastMessage?.seq === message.seq) c.lastMessage = { ...c.lastMessage, body: message.body.slice(0, limits().listPreviewChars) };
      } else if (!message.notice || message.notice.call) {   // a notice says what happened; it is not what was last said, and nothing to read (a call is both)
        const call = message.notice?.call ?? null;
        c.lastMessage = {
          seq: message.seq, body: message.body.slice(0, limits().listPreviewChars),
          createdAt: message.createdAt, sender: message.sender, deleted: message.deleted === true, attachment: message.attachments?.[0]?.kind ?? null, sticker: message.sticker ?? null, call,
        };
        const toRead = call ? missedByMe(call, me?.id ?? '') : message.sender.id !== me?.id && message.deleted !== true;
        if (toRead && !(conversation === openId && deps.visible())) c.unread++;
        if (toRead && mentionsMe(message)) {
          // Not yet read: the list marks it; and in the thread, if you are reading further up, the arrow takes you to it.
          if (!(conversation === openId && deps.visible())) { c.mentioned = true; c.firstMentionSeq ??= message.seq; c.firstMentionId ??= message.id; }
          else if (!threadOf(conversation).stick && !mentionAt.has(conversation)) mentionAt.set(conversation, { id: message.id });
        }
        c.hidden = false;   // something new was said: a hidden conversation is back, as the server has it
        chats = [c, ...(chats ?? []).filter((x) => x !== c)];
      }
    }
    update();
    if (conversation === openId) markRead(conversation);
    if (event.kind === 'created' && (message.notice?.type === 'filesTrimmed' || message.notice?.type === 'messagesTrimmed')) afterTrim(conversation);
  }

  /**
   * The owner trimmed old pictures and files, or old messages, here. The notice does not say which, so what this page holds of the
   * conversation is asked for again: the thread from its newest page (with its pins), and the side panel's lists. The viewer closes.
   */
  function afterTrim(id: string): void {
    mediaLists.delete(id);
    if (viewer?.conversation === id) closeViewer();
    const t = threads.get(id);
    if (t?.loaded) {
      t.messages = []; t.hasMore = false; t.loaded = false;
      if (id === openId) void loadHistory(id);
    }
    if (id === openId && infoOpen) { void loadMedia(id, 'image'); void loadMedia(id, 'file'); }
    update();
  }

  // ---- who has read

  async function fetchReads(id: string): Promise<void> {
    const result = await api.reads(id);
    if (!result.ok) return;   // only a nicety: without it the line just does not show
    threadOf(id).reads = { enabled: result.data.enabled, seqs: new Map(result.data.reads.map((r) => [r.userId, r.seq])) };
    update();
  }

  // ---- muting

  /** Muted now: until turned back on, or for a while that has not ended yet (it ends by itself, also while the page is open). */
  function isMuted(c: Conversation): boolean {
    return c.muted === true && (c.mutedUntil == null || Date.parse(c.mutedUntil) > deps.now().getTime());
  }

  /** The bell: one tap turns a muted conversation back on; otherwise it asks for how long. */
  function onBell(c: Conversation): void {
    if (isMuted(c)) void setMuted(c, false, null);
    else { muting = c.id; update(); }
  }

  /** Zalo's choices, which the family knows. The phone works out the end, so "8 in the morning" is its own morning. */
  const MUTE_CHOICES: ReadonlyArray<readonly [string, string, (now: Date) => Date | null]> = [
    ['1h', say.chat.muteFor1h, (now) => new Date(now.getTime() + 3_600_000)],
    ['4h', say.chat.muteFor4h, (now) => new Date(now.getTime() + 4 * 3_600_000)],
    ['8am', say.chat.muteTill8, (now) => { const d = new Date(now); d.setHours(8, 0, 0, 0); if (d <= now) d.setDate(d.getDate() + 1); return d; }],
    ['always', say.chat.muteAlways, () => null],
  ];

  const muteView = () => {
    const c = muting === null ? undefined : chats?.find((x) => x.id === muting);
    if (c === undefined) return nothing;
    const close = () => { muting = null; update(); };
    return html`
      <dialog class="pcard mutes" aria-labelledby="mute-title" data-mute-panel data-modal @close=${close}
        @keydown=${(e: KeyboardEvent) => { if (e.key === 'Escape') { e.preventDefault(); close(); } }}>
        <div class="pcard-head"><h2 id="mute-title">${say.common.mute}</h2><button class="ib" type="button" aria-label=${say.common.close} @click=${close}>${icons.close()}</button></div>
        <ul class="info-list">${MUTE_CHOICES.map(([key, label, end]) => html`
          <li><button class="info-person" type="button" data-mute-for=${key} @click=${() => { muting = null; void setMuted(c, true, end(deps.now())); }}>${label}</button></li>`)}</ul>
      </dialog>`;
  };

  /**
   * A conversation's own colour and background, for everyone in it: two tabs, each with "each person's own" first, then Save. The
   * dots and tiles are the same ones Settings shows. On a phone it is a sheet from the bottom, like the other dialogs.
   */
  const lookView = () => {
    const l = looking;
    if (l === null) return nothing;
    const close = () => { looking = null; update(); };
    const save = async () => {
      l.busy = true; l.error = null; update();
      const result = await api.setLook(l.id, l.palette, l.pattern);
      if (!result.ok) { l.busy = false; l.error = result; update(); return; }
      const c = chats?.find((x) => x.id === l.id);
      if (c !== undefined) { c.palette = l.palette; c.pattern = l.pattern; }
      close();
    };
    const tab = (id: 'colour' | 'pattern', label: string) => html`
      <button class="tab" type="button" role="tab" data-look-tab=${id} aria-selected=${l.tab === id ? 'true' : 'false'} @click=${() => { l.tab = id; update(); }}>${label}</button>`;
    const colours = html`
      <fieldset class="swatches" data-look-colours><legend class="sr-only">${say.chat.lookColour}</legend>
        <label class="swatch"><input type="radio" name="look-palette" value="" .checked=${liveValue(l.palette === null)} @change=${() => { l.palette = null; update(); }}>
          <span class="dot each" aria-hidden="true"></span><span>${say.chat.lookEach}</span></label>
        ${PALETTES.map((p) => html`
        <label class="swatch"><input type="radio" name="look-palette" value=${p} .checked=${liveValue(l.palette === p)} @change=${() => { l.palette = p; update(); }}>
          <span class="dot" data-palette=${p} aria-hidden="true"></span><span>${say.settings.palettes[p]}</span></label>`)}
      </fieldset>`;
    const patterns = html`
      <fieldset class="patterns" data-look-patterns><legend class="sr-only">${say.chat.lookPattern}</legend>
        <label class="pattern-tile"><input type="radio" name="look-pattern" value="" .checked=${liveValue(l.pattern === null)} @change=${() => { l.pattern = null; update(); }}>
          <span class="backdrop" data-pattern=${storedPattern()} data-palette=${l.palette ?? nothing} aria-hidden="true"></span><span class="name">${say.chat.lookEach}</span></label>
        ${PATTERNS.map((p) => html`
        <label class="pattern-tile"><input type="radio" name="look-pattern" value=${p} .checked=${liveValue(l.pattern === p)} @change=${() => { l.pattern = p; update(); }}>
          <span class="backdrop" data-pattern=${p} data-palette=${l.palette ?? nothing} aria-hidden="true"></span><span class="name">${say.settings.patterns[p]}</span></label>`)}
      </fieldset>`;
    return html`
      <dialog class="pcard look" aria-labelledby="look-title" aria-describedby="look-note" data-look-panel data-modal @close=${close}
        @keydown=${(e: KeyboardEvent) => { if (e.key === 'Escape') { e.preventDefault(); close(); } }}>
        <div class="pcard-head"><button class="ib" type="button" aria-label=${say.common.close} @click=${close}>${icons.close()}</button></div>
        <h2 id="look-title">${say.chat.look}</h2>
        <p class="hint" id="look-note">${say.chat.lookNote}</p>
        <div class="tabs" role="tablist" aria-label=${say.chat.look}>${tab('colour', say.chat.lookColour)}${tab('pattern', say.chat.lookPattern)}</div>
        <div class="look-body" role="tabpanel">${l.tab === 'colour' ? colours : patterns}</div>
        ${errorBox(l.error)}
        <button class="btn" type="button" data-look-save ?disabled=${l.busy} @click=${() => void save()}>${say.chat.lookSave}</button>
      </dialog>`;
  };

  /** The question before an app file is saved. "Không" is the easy answer; "Vẫn tải" saves it as the card's link would have. */
  const appFileView = () => {
    const a = appFile;
    if (a === null) return nothing;
    const close = () => { appFile = null; update(); };
    const save = () => {
      const link = document.createElement('a');
      link.href = `/api/attachments/${a.id}`;
      link.download = a.name;
      link.click();
      close();
    };
    return html`
      <dialog class="pcard appq" aria-labelledby="appfile-q" aria-describedby="appfile-note" data-app-file-panel data-modal @close=${close}
        @keydown=${(e: KeyboardEvent) => { if (e.key === 'Escape') { e.preventDefault(); close(); } }}>
        <h2 id="appfile-q">${say.chat.appFileQ}</h2>
        <p id="appfile-note" class="hint">${say.chat.appFileNote}</p>
        <p class="hint"><b>${a.name}</b></p>
        <div class="pcard-actions">
          <button class="btn secondary" type="button" data-app-file-no @click=${close}>${say.common.no}</button>
          <button class="btn secondary danger-text" type="button" data-app-file-yes @click=${save}>${say.chat.appFileYes}</button>
        </div>
      </dialog>`;
  };

  /** Shows the new state at once, and puts it back with the reason if the server says no. */
  async function setMuted(c: Conversation, muted: boolean, until: Date | null): Promise<void> {
    const before = { muted: c.muted, mutedUntil: c.mutedUntil };
    c.muted = muted;
    c.mutedUntil = until?.toISOString() ?? null;
    update();
    const result = await api.setMute(c.id, muted, until?.toISOString());
    if (result.ok) return;
    Object.assign(c, before);
    handleFailure(result);
  }

  // ---- the "..." menu of a conversation in the list: pin, mark unread, mute, make a group, hide, delete for me (all yours alone)

  function openRowMenu(id: string, at: { x: number; y: number }): void {
    menu = null;
    rowMenu = { id, confirming: false, at: deps.coarsePointer() ? undefined : at };
    update();
    root.querySelector<HTMLElement>('[data-conversation-menu] [role="menuitem"]')?.focus();
  }

  /**
   * A finger held on a row for a second opens its menu (the "..." shows only on the open conversation's row, so the list stays calm).
   * Longer than a message's 450 ms: a row is mostly tapped to open, and a held finger on a list is often the start of a scroll.
   */
  const ROW_PRESS_MS = 1000;
  let rowPress: { timer: number; x: number; y: number } | null = null;
  let rowPressOpened = false;

  function rowPressStart(e: PointerEvent, id: string): void {
    rowPressEnd();
    if (e.pointerType !== 'touch') return;
    const at = { x: e.clientX, y: e.clientY };
    const timer = deps.timer.set(() => { rowPress = null; rowPressOpened = true; openRowMenu(id, at); }, ROW_PRESS_MS);
    rowPress = { timer, ...at };
  }

  function rowPressMove(e: PointerEvent): void {
    if (rowPress !== null && Math.hypot(e.clientX - rowPress.x, e.clientY - rowPress.y) > PRESS_SLOP_PX) rowPressEnd();
  }

  function rowPressEnd(): void {
    if (rowPress === null) return;
    deps.timer.clear(rowPress.timer);
    rowPress = null;
  }

  /** Right-click opens the row's menu there. A finger's long press is handled above: its own menu from the browser is held back. */
  function onRowContext(e: MouseEvent, id: string): void {
    e.preventDefault();
    if (deps.coarsePointer()) return;
    openRowMenu(id, { x: e.clientX, y: e.clientY });
  }

  function closeRowMenu(): void {
    const id = rowMenu?.id;
    rowMenu = null;
    update();
    if (id !== undefined) root.querySelector<HTMLElement>(`[data-row-menu="${id}"]`)?.focus();
  }

  const rowMenuView = () => {
    const c = rowMenu === null ? undefined : chatOf(rowMenu.id);
    if (c === undefined) return nothing;
    const unread = c.unread > 0 || c.markedUnread === true;
    const friend = c.kind === 'direct' && c.peer !== null && !c.peer.deleted && c.peer.gomId ? c.peer.gomId : null;
    return html`
      <div class="scrim" data-scrim @click=${() => dismissMenu(root, closeRowMenu)}></div>
      <div class="menu" role="menu" aria-label=${say.chat.optionsFor(c.title)} data-conversation-menu
        @keydown=${(e: KeyboardEvent) => { if (e.key === 'Escape') dismissMenu(root, closeRowMenu); }}>
      ${rowMenu!.confirming ? html`
        <p>${say.chat.clearQ}</p>
        <div class="ask"><button class="btn small" type="button" data-confirm-clear @click=${() => void clearConversation(c)}>${say.chat.deleteForMe}</button>
        <button class="btn secondary small" type="button" data-cancel-clear @click=${closeRowMenu}>${say.common.cancel}</button></div>` : html`
        <div class="items-grid">
        ${c.pinned ? menuItem('unpin-chat', icons.pin(), say.chat.unpinChat, () => void setPinned(c, false)) : menuItem('pin-chat', icons.pin(), say.chat.pinChat, () => void setPinned(c, true))}
        ${unread ? menuItem('mark-read', icons.checks(), say.chat.markRead, () => void markAllRead(c)) : menuItem('mark-unread', icons.unread(), say.chat.markUnread, () => void setMarkedUnread(c))}
        ${isMuted(c) ? menuItem('unmute-chat', icons.bell(), say.common.unmute, () => { rowMenu = null; onBell(c); })
          : menuItem('mute-chat', icons.bellOff(), say.common.mute, () => { rowMenu = null; onBell(c); })}
        ${friend === null ? nothing : html`<a class="mi" role="menuitem" href=${`/groups/new?with=${encodeURIComponent(friend)}`} data-group-with>${icons.users()}<span>${say.chat.groupWith(c.title)}</span></a>`}
        ${c.hidden ? menuItem('unhide-chat', icons.eye(), say.chat.unhide, () => void rowAction(api.setHidden(c.id, false)))
          : menuItem('hide-chat', icons.eyeOff(), say.chat.hide, () => void hideConversation(c))}
        ${menuItem('clear-chat', icons.trash(), say.chat.clear, () => { rowMenu = { ...rowMenu!, confirming: true }; update(); root.querySelector<HTMLElement>('[data-confirm-clear]')?.focus(); }, true)}
        </div>
        <button class="btn secondary small close-menu" type="button" data-close-row-menu @click=${() => dismissMenu(root, closeRowMenu)}>${say.common.close}</button>`}
      </div>`;
  };

  /** Each action: the server first, then the list as it now is (the other devices are told by the server). */
  async function rowAction(result: Promise<Result<void>>, after?: () => void): Promise<void> {
    rowMenu = null;
    update();
    const done = await result;
    if (!done.ok) {
      if (done.error === 'pin_limit') { notice = say.chat.pinChatLimit; update(); }
      else handleFailure(done);
      return;
    }
    after?.();
    await refreshList();
  }

  const setPinned = (c: Conversation, on: boolean) => rowAction(api.setPinned(c.id, on));
  const setMarkedUnread = (c: Conversation) => rowAction(api.setMarkedUnread(c.id, true));

  /** "Mark as read": the read position moves to the end, which also clears a mark. */
  const markAllRead = (c: Conversation) => rowAction(api.markRead(c.id, Math.max(c.lastSeq, c.lastReadSeq)));

  /** Opening a hidden conversation brings it back to the list: on the page at once, then on the server (which tells your other devices). */
  function unhide(c: Conversation): void {
    c.hidden = false;
    void api.setHidden(c.id, false).then((done) => { if (!done.ok) handleFailure(done); });
  }

  /** Leaves the list (and closes it, if it is open) until something new is said. */
  const hideConversation = (c: Conversation) => rowAction(api.setHidden(c.id, true), () => { forget(c.id, false); offerUndoHide(c.id); });

  /** For a moment after hiding: "Hoàn tác", for the tap that was a mistake. */
  function offerUndoHide(id: string): void {
    if (undoHide !== null) deps.timer.clear(undoHide.timer);
    undoHide = { id, timer: deps.timer.set(() => { undoHide = null; update(); }, UNDO_HIDE_MS) };
  }

  function undoHiding(): void {
    if (undoHide === null) return;
    deps.timer.clear(undoHide.timer);
    const { id } = undoHide;
    undoHide = null;
    void rowAction(api.setHidden(id, false));
  }

  const undoView = () => undoHide === null ? nothing : html`
    <div class="toast" role="status" data-undo-hide><span>${say.chat.hiddenToast}</span>
      <button class="btn secondary small" type="button" data-undo @click=${undoHiding}>${say.common.undo}</button></div>`;

  const clearConversation = (c: Conversation) => rowAction(api.clearConversation(c.id), () => forget(c.id, true));

  /** Takes a conversation out of the list; with `thread`, what this page holds of its messages goes too. */
  function forget(id: string, thread: boolean): void {
    chats = (chats ?? []).filter((x) => x.id !== id);
    if (thread) { const t = threads.get(id); if (t !== undefined) { t.messages = []; t.pins = []; t.hasMore = false; t.loaded = false; } mediaLists.delete(id); }
    if (openId === id) { deps.nav.push('/'); openId = null; }
    update();
  }

  // ---- opening the menu without the button

  /** Puts the open menu where it was asked for, after drawing (`menu.ts`). */
  function placeMenu(): void {
    placeMenuAt(root.querySelector<HTMLElement>(rowMenu !== null ? '.menu[data-conversation-menu]' : '.menu'), rowMenu !== null ? rowMenu.at : menu?.at);
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

  /**
   * Swipe to reply (a phone): a finger that drags a message sideways, someone else's to the right and your own to the left, moves it
   * with the finger and brings in a round reply arrow on the side it leaves; letting go past `SWIPE_READY` starts the reply, as
   * "Trả lời" in the menu does. A finger that first moves up or down is scrolling, and one that moves the other way is not a swipe.
   * The row moves through a custom property (the page's policy allows no style attribute) and only while it is dragged.
   */
  let swipe: { row: HTMLElement; m: Shown; mine: boolean; x: number; y: number; moved: number; live: boolean } | null = null;

  function swipeStart(e: PointerEvent, m: Shown, mine: boolean, hasMenu: boolean): void {
    swipeEnd(false);
    if (!hasMenu || e.pointerType !== 'touch' || !deps.coarsePointer()) return;
    const row = (e.currentTarget as Element | null)?.closest<HTMLElement>('.mrow') ?? null;
    if (row !== null) swipe = { row, m, mine, x: e.clientX, y: e.clientY, moved: 0, live: false };
  }

  function swipeMove(e: PointerEvent): void {
    if (swipe === null) return;
    const dx = e.clientX - swipe.x;
    const dy = e.clientY - swipe.y;
    if (!swipe.live) {
      if (Math.hypot(dx, dy) <= PRESS_SLOP_PX) return;
      if (Math.abs(dy) >= Math.abs(dx) || swiped(dx, swipe.mine) === 0) { swipe = null; return; }
      swipe.live = true;
      lastTap = null;
      swipe.row.classList.add('swiping');
    }
    swipe.moved = swiped(dx, swipe.mine);
    swipe.row.style.setProperty('--swipe', `${swipe.mine ? -swipe.moved : swipe.moved}px`);
    swipe.row.style.setProperty('--swipe-on', String(Math.min(1, swipe.moved / SWIPE_READY)));
    swipe.row.toggleAttribute('data-swipe-ready', swipeReady(swipe.moved));
  }

  /** The finger lifted (`release`) or the touch was taken away (a scroll the browser started, a call). */
  function swipeEnd(release: boolean): void {
    const s = swipe;
    swipe = null;
    if (s === null || !s.live) return;
    const from = s.mine ? -s.moved : s.moved;
    s.row.classList.remove('swiping');
    s.row.removeAttribute('data-swipe-ready');
    s.row.style.removeProperty('--swipe');
    s.row.style.removeProperty('--swipe-on');
    // The row comes back: it was moved only while dragged, so there is nothing left to undo at rest.
    if (typeof s.row.animate === 'function' && motionAllowed()) s.row.animate([{ left: `${from}px` }, { left: '0px' }], { duration: 160, easing: 'ease-out' });
    if (release && swipeReady(s.moved)) startReply(s.m);
  }

  /** Two taps on one message this close together (in milliseconds) are a double tap. */
  const DOUBLE_TAP_MS = 300;
  let lastTap: { id: string; at: number } | null = null;

  /**
   * A double tap with a finger hearts the message, as Messenger does: it adds ❤️ (and replaces another reaction of yours) and never takes
   * one back. A tap that has a meaning of its own (a picture, a link, a quote, a button, a player) is not counted. Called as the finger
   * lifts, before the press is ended: a press is kept only for a finger, and still there means it was a plain tap, not a hold or a scroll.
   */
  function tapEnd(e: PointerEvent, m: Shown): void {
    if (press === null) return;
    const first = lastTap;
    lastTap = null;
    if ((e.target as Element | null)?.closest?.('a, button, video, audio')) return;
    const at = deps.now().getTime();
    if (first === null || first.id !== m.id || at - first.at > DOUBLE_TAP_MS) { lastTap = { id: m.id, at }; return; }
    if (m.reactions?.some((r) => r.emoji === HEART && r.users.some((u) => u.id === me?.id))) return;
    void setReaction(m, HEART);
  }

  // ---- editing

  /** Puts the words of one of your messages in the composer; sending then changes that message instead of making a new one. */
  function startEdit(m: Shown): void {
    menu = null;
    const t = threadOf(m.conversationId);
    t.editing = { id: m.id };
    t.replying = null;
    t.draft = m.body;
    const known = new Map<string, string>();
    for (const who of m.mentions ?? []) { const name = who === me?.id ? me.displayName : people.get(m.conversationId)?.find((p) => p.id === who)?.displayName; if (name !== undefined) known.set(who, name); }
    picked.set(m.conversationId, known);
    update();
    const field = root.querySelector<HTMLTextAreaElement>('.composer textarea');
    field?.focus();
  }

  async function saveEdit(id: string): Promise<void> {
    const t = threadOf(id);
    const body = t.draft.trim();
    if (t.editing === null || body === '') return;
    const target = t.editing.id;
    const mentions = mentionIds(id, body);
    const result = mentions.length > 0 ? await api.edit(id, target, body, mentions) : await api.edit(id, target, body);
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
    const most = limits().quoteChars;
    t.replying = { id: m.id, sender: m.sender, snippet: body.length > most ? body.slice(0, most).join('') : m.body, ...(m.sticker ? { sticker: m.sticker } : {}),
      ...(m.attachments?.length ? { attachment: m.attachments[0]!.kind } : {}) };
    update();
    root.querySelector<HTMLTextAreaElement>('.composer textarea')?.focus();
  }

  async function copyText(m: Shown): Promise<void> {
    menu = null;
    let text: string;
    try { await navigator.clipboard.writeText(m.body); text = say.chat.copied; } catch { text = say.chat.copyFailed; }
    if (copyNote !== null) deps.timer.clear(copyNote.timer);
    copyNote = { text, timer: deps.timer.set(() => { copyNote = null; update(); }, COPY_NOTE_MS) };
    update();
  }

  /** Opens the dialog for passing on these words, pictures and files, or a sticker (a whole message, or one picture from the viewer). */
  function startForward(body: string, attachments: Attachment[], sticker: string | null = null): void {
    menu = null;
    forwarding = { body, attachments, sticker, picked: [], q: '', note: '', busy: false };
    update();
    root.querySelector<HTMLInputElement>('[data-forward-search]')?.focus();
  }

  type Passed = { kind: 'sent' | 'queued' } | { kind: 'failed'; why: string } | { kind: 'signed-out' };

  /** Passes it on to each ticked conversation in turn, then says in one line how that went. */
  async function sendForward(): Promise<void> {
    const f = forwarding;
    if (f === null || f.busy || f.picked.length === 0 || me === null) return;
    f.busy = true;
    notice = f.attachments.length > 0 ? say.chat.forwarding : '';
    update();
    const sent: string[] = [], queued: string[] = [], failed: { name: string; why: string }[] = [];
    for (const target of f.picked) {
      const name = chatOf(target)?.title ?? '';
      const r = await forwardTo(f, target, name);
      if (r.kind === 'signed-out') return;
      if (r.kind === 'failed') failed.push({ name, why: r.why }); else if (r.kind === 'sent') sent.push(name); else queued.push(name);
    }
    forwarding = null;
    const one = f.picked.length === 1;
    notice = [
      sent.length > 0 ? say.chat.forwardedTo(sent.join(', ')) : '',
      queued.length > 0 ? say.chat.forwardQueued(queued.join(', ')) : '',
      ...failed.map((x) => one ? x.why : `${x.name}: ${x.why}`),
    ].filter((x) => x !== '').join(' ');
    update();
  }

  /**
   * Sends the words, pictures and files to one conversation as a new message from you, and then the note, if any. The pictures and
   * files are copied by the server first (so that needs a connection: with none, nothing is queued and you are told); the messages
   * then use the same queue as writing, so a lost connection does not lose them. A refusal for good (no longer friends, say) is told
   * here, not left in a thread you are not in.
   */
  async function forwardTo(f: NonNullable<typeof forwarding>, target: string, name: string): Promise<Passed> {
    const t = threadOf(target);
    const copies: Attachment[] = [];
    for (const a of f.attachments) {
      const copy = await api.copyAttachment(target, a.id);
      if (copy.ok) { copies.push(copy.data); continue; }
      if (copy.error === 'unauthorized') { go('/sign-in'); return { kind: 'signed-out' }; }
      return { kind: 'failed', why: copy.error === 'network' ? say.chat.forwardNeedsNetwork(name) : errorText(copy) };
    }
    const queue = (body: string, files: Attachment[], sticker: string | null = null): string => {
      const clientId = deps.newId();
      const waiting = t.pending.some((x) => x.state === 'queued');
      t.pending.push({
        id: clientId, clientId, seq: 0, conversationId: target, sender: { id: me!.id, displayName: me!.displayName }, body,
        createdAt: deps.now().toISOString(), state: waiting ? 'queued' : 'sending', ...(files.length > 0 ? { attachments: files } : {}), ...(sticker ? { sticker } : {}),
      });
      persist();
      update();
      return clientId;
    };
    /** Sends what is queued, and says how this one fared; one refused for good is taken out again. */
    const outcome = async (clientId: string): Promise<Passed> => {
      if (!t.pending.some((x) => x.clientId === clientId && x.state === 'queued')) await flush(target);
      const p = t.pending.find((x) => x.clientId === clientId);
      if (p === undefined) return { kind: 'sent' };
      if (p.state !== 'failed') return { kind: 'queued' };
      t.pending = t.pending.filter((x) => x.clientId !== clientId);
      persist();
      return { kind: 'failed', why: p.error ? errorText(p.error) : say.chat.forwardFailed(name) };
    };
    const passed = await outcome(queue(f.body, copies, f.sticker));
    // The note goes after what it is about: not at all if that was refused, and behind it in the queue if that is waiting for the network.
    const note = f.note.trim();
    if (note === '' || passed.kind === 'failed') return passed;
    const said = await outcome(queue(note, []));
    return said.kind === 'sent' ? passed : said;
  }

  // ---- someone's card

  /** Opens the card of this person, showing what was fetched before while it asks again. */
  function openCard(userId: string): void {
    menu = null;
    card = { userId, data: cards.get(userId) ?? null, error: null, asking: null, busy: false };
    update();
    void loadCard(userId);
  }

  /** Fetches a card, keeps it for the 1:1 header, and shows it if that card is still the one open. Quietly does nothing more on a failure when no card is open. */
  async function loadCard(userId: string): Promise<void> {
    const result = await api.person(userId);
    if (result.ok) {
      cards.set(userId, result.data);
      if (card?.userId === userId) { card.data = result.data; card.error = null; }
    } else if (result.error === 'unauthorized') { go('/sign-in'); return; }
    else if (card?.userId === userId) card.error = result;
    update();
  }

  /** A friend request, unfriending or blocking from the card: then the card, the group's members and the list are asked again. */
  async function cardAction(call: () => Promise<{ ok: true } | Failure>): Promise<void> {
    const k = card;
    if (k === null || k.busy) return;
    k.busy = true;
    update();
    const result = await call();
    k.busy = false;
    k.asking = null;
    if (!result.ok) {
      if (result.error === 'unauthorized') { go('/sign-in'); return; }
      k.error = result;
      update();
      return;
    }
    k.error = null;
    for (const id of people.keys()) people.delete(id);
    if (openId !== null) void loadPeople(openId);
    void refreshList();
    await loadCard(k.userId);
  }

  /** "Nhắn tin" on a friend's card: their 1:1 conversation, made if it is not there yet. */
  async function messageFrom(gomId: string): Promise<void> {
    const k = card;
    if (k === null) return;
    k.busy = true;
    update();
    const result = await api.openDirect(gomId);
    k.busy = false;
    if (!result.ok) {
      if (result.error === 'unauthorized') { go('/sign-in'); return; }
      k.error = result;
      update();
      return;
    }
    card = null;
    if (chatOf(result.data.id) === undefined) await refreshList();
    open(result.data.id);
  }

  async function leaveGroup(id: string): Promise<void> {
    const result = await api.leaveGroup(id);
    leaving = false;
    if (!result.ok) { handleFailure(result); return; }
    chats = (chats ?? []).filter((c) => c.id !== id);
    deps.nav.push('/');
    openId = null;
    update();
    void refreshList();
  }

  // ---- searching

  /** Searches the open conversation once typing pauses; emptied, the results go. A character or two waits for Enter. */
  function searchSoon(s: NonNullable<typeof searching>): void {
    if (threadSearchTimer !== null) deps.timer.clear(threadSearchTimer);
    threadSearchTimer = null;
    const q = s.q.trim();
    if (q === '') { s.ticket++; s.results = null; s.asked = ''; update(); return; }
    if ([...q].length < SEARCH_AS_YOU_TYPE_CHARS) return;
    threadSearchTimer = deps.timer.set(() => { threadSearchTimer = null; void runSearch(); }, SEARCH_WAIT_MS);
  }

  function searchNow(): void {
    if (threadSearchTimer !== null) deps.timer.clear(threadSearchTimer);
    threadSearchTimer = null;
    void runSearch();
  }

  /** Asks for your friends once a search above the list starts (again for each new search, as friends come and go). */
  function loadFriends(): void {
    if (filter.trim() === '') { friends = null; return; }
    if (friends !== null) return;
    friends = [];
    void api.friends().then((result) => {
      if (!result.ok || filter.trim() === '') return;
      friends = result.data.friends;
      update();
    });
  }

  /** A friend found by the search, not written to yet: their conversation, made now, opened, and the search done. */
  async function chatWith(gomId: string): Promise<void> {
    const result = await api.openDirect(gomId);
    if (!result.ok) { handleFailure(result); return; }
    filter = '';
    friends = null;
    if (chatOf(result.data.id) === undefined) await refreshList();
    open(result.data.id);
  }

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

  /**
   * Goes to a message (found by the search, pinned, or a picture's): older pages are loaded until it is there, then it is brought
   * into view and marked for a moment. On a phone the information page covers the thread, so it is closed first.
   */
  async function reveal(m: { id: string; conversationId: string }): Promise<void> {
    closeInfoPage();
    const id = m.conversationId;
    const t = threadOf(id);
    searching = null;
    if (!t.loaded) await loadHistory(id);   // just opened from the list's search
    t.pinsOpen = false;
    t.stick = false;
    t.jump = false;
    update();
    if (!t.messages.some((x) => x.id === m.id) && t.hasMore) await loadOlder(id, m.id);   // further back than the server reaches in one jump: not found
    found = m.id;
    update();
    const box = root.querySelector<HTMLElement>('.msgs');
    const row = root.querySelector<HTMLElement>(`[data-msg="${m.id}"]`);
    if (box !== null && row !== null) scrollWithin(box, row, 'center');
    deps.timer.set(() => { if (found === m.id) { found = null; update(); } }, 2500);
  }

  // ---- deleting a message for yourself

  /** Takes a message out of this device's view: it goes from the thread, and the list is asked again (the preview may have changed). */
  function drop(conversation: string, messageId: string): void {
    if (viewer?.conversation === conversation) viewerSync = true;
    const lists = mediaLists.get(conversation);
    if (lists !== undefined) for (const k of ['image', 'file'] as const) lists[k].items = lists[k].items.filter((i) => i.messageId !== messageId);
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

  /** False only when the server says it takes no uploads; an answer without the field (an older server) means yes. */
  const uploadsOn = (): boolean => me?.uploads !== false;

  /** Puts what was chosen, pasted or dropped in the tray and starts sending it up. */
  function addFiles(id: string, files: File[]): void {
    const t = threadOf(id);
    if (t.editing !== null || files.length === 0 || !uploadsOn()) return;
    const most = limits().attachments;
    const room = most - t.tray.length;
    for (const file of files.slice(0, Math.max(0, room))) t.tray.push({ key: ++trayKey, name: file.name || say.chat.fileFallbackName, file, state: 'waiting', progress: 0 });
    if (files.length > room) { notice = tooManyAttachments(most); }
    update();
    pumpTray(id);
  }

  // A keyboard's picture goes where a pasted one would: into the open conversation, only while its message box has the focus.
  deps.keyboardImages?.((file) => {
    if (openId === null || !uploadsOn()) return;
    const box = root.querySelector('textarea[name="message"]');
    if (box === null || root.ownerDocument.activeElement !== box) return;
    addFiles(openId, [file]);
  });

  function onPaste(e: ClipboardEvent): void {
    const files = [...(e.clipboardData?.files ?? [])];
    if (files.length === 0 || openId === null || !uploadsOn()) return;   // text pastes as text
    e.preventDefault();
    addFiles(openId, files);
  }

  /** Keeps a couple of uploads going until every waiting one has gone up (or failed). */
  function pumpTray(id: string): void {
    const t = threadOf(id);
    for (;;) {
      if (t.tray.filter((i) => i.state === 'uploading' || i.state === 'converting').length >= UPLOADS_AT_ONCE) return;
      const next = t.tray.find((i) => i.state === 'waiting');
      if (next === undefined) return;
      void uploadOne(id, t, next);
    }
  }


  /**
   * Progress for a tray item: the bar moves in steps of a twentieth. Each step draws the whole page again (every message on screen),
   * and an upload or a video being made smaller reports far more often than that.
   */
  const progressOf = (item: TrayItem) => (fraction: number): void => {
    const step = Math.floor(fraction * PROGRESS_STEPS);
    const was = Math.floor(item.progress * PROGRESS_STEPS);
    item.progress = fraction;
    if (step !== was) update();
  };
  async function uploadOne(id: string, t: Thread, item: TrayItem): Promise<void> {
    if (!item.prepared && isVideoFile(item.file!)) {
      if (!await prepare(item)) { pumpTray(id); return; }
      if (!t.tray.includes(item)) { pumpTray(id); return; }
    }
    item.state = 'uploading';
    item.progress = 0;
    update();
    const result = await api.upload(id, item.file!, progressOf(item));
    if (!t.tray.includes(item)) { pumpTray(id); return; }   // taken out while it was going up
    // A video's still picture follows it; without one it still plays (it shows nothing until then).
    if (result.ok && result.data.kind === 'video' && item.poster) await api.setPoster(result.data.id, item.poster);
    if (result.ok && item.voice) {
      t.tray = t.tray.filter((x) => x !== item);
      update();
      pumpTray(id);
      await post(id, { body: '', attachments: [result.data] });
      return;
    }
    if (result.ok) { item.state = 'done'; item.attachment = result.data; item.file = null; delete item.error; }
    else if (result.error === 'unauthorized') { go('/sign-in'); return; }
    else { item.state = 'failed'; item.error = result; }
    update();
    pumpTray(id);
  }

  /**
   * Makes a picked video smaller on this device, once, to fit the limit. When this device cannot, or the video is too long to fit, it
   * goes as it is if it is within the limit (the server keeps it as a video when it is an MP4, as a file when not); over the limit it
   * fails here, before any upload.
   * False when it failed.
   */
  async function prepare(item: TrayItem): Promise<boolean> {
    const original = item.file!;
    item.state = 'converting';
    item.progress = 0;
    update();
    let ready: PreparedVideo | null = null;
    try { ready = await deps.prepareVideo?.(original, progressOf(item), limits().videoBytes) ?? null; } catch { ready = null; }
    item.prepared = true;
    if (ready !== null) {
      item.file = new File([ready.video], mp4Name(original.name), { type: 'video/mp4' });
      item.poster = ready.poster;
      if (ready.poster !== null) item.preview = await asDataUrl(ready.poster);
    }
    if (item.file!.size > limits().videoBytes) {
      item.state = 'failed';
      item.error = { ok: false, status: 413, error: 'video_too_large' };
      item.file = original;
      item.prepared = false;   // a retry tries the device again
      update();
      return false;
    }
    return true;
  }

  function retryTray(item: TrayItem): void {
    if (item.file === null) return;
    item.state = 'waiting'; delete item.error;
    update();
    if (openId !== null) pumpTray(openId);
  }

  // ---- disappearing messages

  /** Takes away the messages whose time is up (the server deletes them too, within half a minute), and pins of them. */
  function dropExpired(): void {
    const now = deps.now().getTime();
    const gone = (m: { expiresAt?: string | null }) => m.expiresAt != null && Date.parse(m.expiresAt) <= now;
    for (const t of threads.values()) {
      if (t.messages.some(gone)) t.messages = t.messages.filter((m) => !gone(m));
      if (t.pins.some(gone)) t.pins = t.pins.filter((m) => !gone(m));
    }
  }

  let expiryTimer: number | null = null;
  let expiryAt = Infinity;
  /** One timer, for the next message due anywhere in what is loaded: at its time the page is drawn again without it. */
  function scheduleExpiry(): void {
    const now = deps.now().getTime();
    let soonest = Infinity;
    for (const t of threads.values()) for (const m of t.messages) {
      if (m.expiresAt == null) continue;
      const at = Date.parse(m.expiresAt);
      if (at > now && at < soonest) soonest = at;
    }
    if (soonest === expiryAt) return;
    if (expiryTimer !== null) deps.timer.clear(expiryTimer);
    expiryTimer = null;
    expiryAt = soonest;
    if (soonest !== Infinity) expiryTimer = deps.timer.set(() => { expiryTimer = null; expiryAt = Infinity; update(); }, Math.min(soonest - now, 2 ** 31 - 1));
  }

  // ---- voice messages

  /** The microphone takes the send button's place while there is nothing to send, as in Messenger: the composer keeps its width. */
  const voiceOffered = (t: Thread): boolean => deps.recordVoice !== undefined && uploadsOn() && t.editing === null && t.draft.trim() === '' && t.tray.length === 0;

  /** Where the composer was: throw it away, how long so far out of the most, and send. */
  const recordingBar = (r: VoiceRecording) => html`
    <div class="composer recbar" data-recording role="group" aria-label=${say.chat.recordingVoice}>
      <button class="ib" type="button" data-voice-cancel aria-label=${say.chat.discardVoice} @click=${cancelVoice}>${icons.trash()}</button>
      <span class="rec-dot" aria-hidden="true"></span>
      <span class="rec-time" data-voice-time role="timer">${clockOf(r.seconds)} / ${clockOf(VOICE_SECONDS)}</span>
      <button class="send" type="button" data-voice-send aria-label=${say.chat.sendVoice} ?disabled=${r.recorder === null} @click=${() => void finishVoice()}>${icons.send()}</button>
    </div>`;

  /** Asks for the microphone and starts recording in this conversation; a "no" says why, in words. */
  async function startVoice(id: string): Promise<void> {
    if (recording !== null || deps.recordVoice === undefined || threadOf(id).editing !== null) return;
    const r: VoiceRecording = { conversation: id, recorder: null, seconds: 0, timer: null };
    recording = r;
    picker = null;
    update();
    const started = await deps.recordVoice();
    if (recording !== r) { if (typeof started !== 'string') started.cancel(); return; }   // thrown away while the browser was asking
    if (typeof started === 'string') {
      recording = null;
      notice = started === 'denied' ? say.chat.micDenied : say.chat.voiceUnsupported;
      update();
      return;
    }
    r.recorder = started;
    r.timer = deps.timer.set(tick, 1000);
    update();
    function tick(): void {
      if (recording !== r) return;
      r.seconds++;
      if (r.seconds >= VOICE_SECONDS) { void finishVoice(); return; }
      r.timer = deps.timer.set(tick, 1000);
      update();
    }
  }

  function cancelVoice(): void {
    const r = recording;
    if (r === null) return;
    recording = null;
    if (r.timer !== null) deps.timer.clear(r.timer);
    r.recorder?.cancel();
    update();
  }

  /** Stops recording and sends it: it goes up through the tray, so a failure there can be tried again, and is sent once it is up. */
  async function finishVoice(): Promise<void> {
    const r = recording;
    if (r === null || r.recorder === null) return;
    recording = null;
    if (r.timer !== null) deps.timer.clear(r.timer);
    update();
    const sound = await r.recorder.stop();
    if (sound === null) { notice = say.chat.voiceFailed; update(); return; }
    const t = threadOf(r.conversation);
    const name = say.chat.voiceFileName(voiceStamp(deps.now()));
    t.tray.push({ key: ++trayKey, name, file: new File([sound], name, { type: 'audio/wav' }), state: 'waiting', progress: 0, voice: true });
    update();
    pumpTray(r.conversation);
  }

  // ---- writing

  /** The whole outbox, written down so a reload or a discarded tab does not lose what has not gone yet. */
  function persist(): void {
    if (outbox === null) return;
    outbox.save([...threads].flatMap(([id, t]) => t.pending.filter((p) => p.state !== 'failed')
      .map((p) => ({ conversation: id, clientId: p.clientId, body: p.body, createdAt: p.createdAt, ...(p.reply ? { replyTo: p.reply } : {}), ...(p.attachments && p.attachments.length > 0 ? { attachments: p.attachments } : {}), ...(p.sticker ? { sticker: p.sticker } : {}), ...(p.mentions && p.mentions.length > 0 ? { mentions: p.mentions } : {}) }))));
  }

  async function send(id: string): Promise<void> {
    const t = threadOf(id);
    if (t.editing !== null) { await saveEdit(id); return; }
    const body = t.draft.trim();
    const ready = t.tray.filter((i) => i.state === 'done' && i.attachment !== undefined).map((i) => i.attachment!);
    if ((body === '' && ready.length === 0) || me === null) return;
    if (t.tray.some((i) => i.state !== 'done')) return;   // still going up, or failed: remove or retry it first
    const mentions = mentionIds(id, body);
    setDraft(id, '');
    t.tray = [];
    await post(id, { body, ...(ready.length > 0 ? { attachments: ready } : {}), ...(mentions.length > 0 ? { mentions } : {}) });
  }

  /** Sends a sticker at once (as the answer, if one is being written). Like words, it waits behind anything still on its way. */
  async function sendSticker(id: string, code: string): Promise<void> {
    if (me === null || threadOf(id).editing !== null) return;
    rememberSticker(code);
    await post(id, { body: '', sticker: code });
  }

  /** Puts a message of yours in the conversation, as the answer if one is being written, and sends it. */
  async function post(id: string, what: { body: string; attachments?: Attachment[]; sticker?: string; mentions?: string[] }): Promise<void> {
    const t = threadOf(id);
    const reply = t.replying;
    t.replying = null;
    const clientId = deps.newId();
    // If an earlier message is still waiting, this one waits behind it: they must reach the server in the order written.
    const waiting = t.pending.some((x) => x.state === 'queued');
    t.pending.push({
      id: clientId, clientId, seq: 0, conversationId: id, sender: { id: me!.id, displayName: me!.displayName }, ...what,
      createdAt: deps.now().toISOString(), state: waiting ? 'queued' : 'sending', ...(reply ? { reply } : {}),
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
        const result = p.sticker ? await api.sendSticker(id, p.clientId, p.sticker, p.reply?.id)
          : p.mentions && p.mentions.length > 0 ? await api.send(id, p.clientId, p.body, p.reply?.id, files.length > 0 ? files : undefined, p.mentions)
          : files.length > 0 ? await api.send(id, p.clientId, p.body, p.reply?.id, files) : await api.send(id, p.clientId, p.body, p.reply?.id);
        if (result.ok) {
          t.failures = 0;
          t.attempts.delete(p.clientId);
          apply(id, { seq: result.data.seq, kind: 'created', message: result.data });
          persist();
          continue;
        }
        if (result.error === 'unauthorized') { p.state = 'queued'; update(); go('/sign-in'); return; }
        // The server wants a newer page: it waits, kept in the outbox, and goes once the page has reloaded.
        if (result.error === 'update_required') { p.state = 'queued'; p.error = result; persist(); update(); return; }

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
        sender: { id: me.id, displayName: me.displayName }, body: item.body, createdAt: item.createdAt, state: 'queued', ...(item.replyTo ? { reply: item.replyTo } : {}), ...(item.attachments && item.attachments.length > 0 ? { attachments: item.attachments } : {}), ...(item.sticker ? { sticker: item.sticker } : {}), ...(item.mentions && item.mentions.length > 0 ? { mentions: item.mentions } : {}),
      });
    }
    persist();
    update();
    for (const [id, t] of threads) if (t.pending.length > 0) void flush(id);
  }

  // ---- start

  const handlers: LiveHandlers = {
    call: (frame) => deps.calls?.frame(frame),
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
    event: (conversation, event) => {
      if (event.kind === 'created') stopTyping(conversation, event.message.sender.id);   // what they were writing has arrived
      apply(conversation, event);
    },
    typing: (conversation, userId, name) => showTyping(conversation, userId, name),
    conversations() {
      void refreshList();
      if (openId !== null) void fetchReads(openId);   // a switch may have been flipped by someone
    },
    hidden: (conversation, messageId) => drop(conversation, messageId),
    // Deleted for you on another device: what this page holds goes, and an open thread asks again (it is empty now, for you).
    cleared: (conversation) => afterTrim(conversation),
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
    slow(conversation) {
      // Too many catch-up requests too fast: wait a moment and ask again from where this device really is (one wait per conversation).
      if (slowWaiting.has(conversation)) return;
      slowWaiting.add(conversation);
      deps.timer.set(() => {
        slowWaiting.delete(conversation);
        const t = threads.get(conversation);
        if (t?.loaded) live?.sync(conversation, t.cursor);
      }, SLOW_RETRY_MS);
    },
    ended: () => go('/sign-in'),
    replaced() { notice = say.chat.replaced; update(); },
    // Not reconnecting on purpose: the page must reload, and the update bar says so; "reconnecting" would not be true.
    tooOld() { offline = false; update(); },
  };

  // Back from the information page (its own step in the history) only closes it.
  deps.nav.onPop(() => { infoPage = false; const id = idFromPath(); if (id === null) toList(); else show(id); });
  document.addEventListener('visibilitychange', () => {
    if (!deps.visible()) return;
    live?.retryNow();
    retryAll();
    if (openId !== null) { markRead(openId); deps.clearNotifications?.(openId); }
  });

  addEventListener('online', () => { live?.retryNow(); retryAll(); });

  // A press anywhere outside the emoji and sticker panel (and its button, which toggles it) closes the panel.
  document.addEventListener('pointerdown', (e) => {
    if (picker === null) return;
    const target = e.target as Element | null;
    if (target?.closest?.('[data-picker], [data-picker-toggle]')) return;
    picker = null;
    update();
  }, true);

  update();
  void api.me().then(async (result) => {
    if (!result.ok) { handleFailure(result); return; }
    // Someone who asked to join and is not yet a member sees only the waiting screen: no list, no socket.
    if ((result.data.standing ?? 'member') !== 'member') {
      mountWaiting(root, api, go, result.data, (check) => document.addEventListener('visibilitychange', () => { if (deps.visible()) check(); }));
      return;
    }
    me = result.data;
    rememberAppCalls(me.appCalls === true);
    outbox = deps.outbox(me.gomId);
    drafts = deps.drafts?.(me.gomId) ?? null;
    for (const [id, t] of threads) if (t.draft === '' && t.editing === null) t.draft = drafts?.load(id) ?? '';
    // A reload loses files picked but not sent, and a change being made to a sent message; typed words are kept (drafts).
    deps.holdReload?.(() => deps.calls?.busy() === true || [...threads.values()].some((t) => t.tray.length > 0 || t.editing !== null) || recording !== null);
    live = deps.live(handlers);
    live.start();
    await refreshList();
    restoreOutbox();
    if (openId !== null) show(openId);
    void offerPush();
  });
}
