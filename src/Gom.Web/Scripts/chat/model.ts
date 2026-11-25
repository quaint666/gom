import type { ChatMessage, Failure, MediaItem } from '../api.js';
import { say } from '../say.js';

/** A message as the thread shows it: stored, or still on its way (no sequence number yet). */
export type Shown = ChatMessage & { state?: 'sending' | 'queued' | 'failed'; error?: Failure };

export type ThreadItem =
  | { kind: 'day'; key: string; label: string }
  | { kind: 'new'; key: string; count: number }
  | { kind: 'notice'; key: string; msg: Shown }
  | { kind: 'msg'; key: string; msg: Shown; mine: boolean; first: boolean; last: boolean };

const RUN_GAP_MS = 5 * 60 * 1000;
const graphemes = new Intl.Segmenter(undefined, { granularity: 'grapheme' });

/** One or two letters for an avatar: the first letter of the first and the last word. */
export function initials(name: string): string {
  const words = name.trim().split(/\s+/).filter((w) => w !== '');
  if (words.length === 0) return '?';
  const first = (w: string): string => [...graphemes.segment(w)][0]?.segment.toLocaleUpperCase('vi') ?? '';
  return words.length === 1 ? first(words[0]!) : first(words[0]!) + first(words[words.length - 1]!);
}

/** How many colours an avatar can have (the classes a1 to a6 in the stylesheet). */
export const AVATAR_TONES = 6;

/** The same name always gets the same colour, so a person is easy to spot; it changes only when they rename themselves. */
export function avatarTone(name: string): number {
  let sum = 0;
  for (const ch of name.trim().toLocaleLowerCase('vi')) sum = (sum * 31 + ch.codePointAt(0)!) % 100_003;
  return (sum % AVATAR_TONES) + 1;
}

const pad = (n: number): string => String(n).padStart(2, '0');
const sameDay = (a: Date, b: Date): boolean => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
const dayKey = (d: Date): string => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
const yesterdayOf = (now: Date): Date => new Date(now.getFullYear(), now.getMonth(), now.getDate() - 1, 12);

export function clock(iso: string): string {
  const d = new Date(iso);
  return `${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** The label on a day pill. */
export function dayLabel(iso: string, now: Date): string {
  const d = new Date(iso);
  if (sameDay(d, now)) return say.notices.today;
  if (sameDay(d, yesterdayOf(now))) return say.notices.yesterday;
  return `${pad(d.getDate())}/${pad(d.getMonth() + 1)}/${d.getFullYear()}`;
}

/** The short time in a list row: a clock today, then "Hôm qua", then the date. */
export function listTime(iso: string, now: Date): string {
  const d = new Date(iso);
  if (sameDay(d, now)) return clock(iso);
  if (sameDay(d, yesterdayOf(now))) return say.notices.yesterday;
  return d.getFullYear() === now.getFullYear() ? `${d.getDate()}/${d.getMonth() + 1}` : `${d.getDate()}/${d.getMonth() + 1}/${d.getFullYear()}`;
}

/** Both lists in sequence order, no repeats; a later copy of the same message wins. Neither input is changed. */
export function mergeMessages(have: readonly ChatMessage[], incoming: readonly ChatMessage[]): ChatMessage[] {
  const bySeq = new Map<number, ChatMessage>();
  for (const m of have) bySeq.set(m.seq, m);
  for (const m of incoming) bySeq.set(m.seq, m);
  return [...bySeq.values()].sort((a, b) => a.seq - b.seq);
}

/** The side panel's list with these added: newest message first, each picture or file once (a later copy wins). Neither input is changed. */
export function mergeMedia(have: readonly MediaItem[], incoming: readonly MediaItem[]): MediaItem[] {
  const byId = new Map<string, MediaItem>();
  for (const i of have) byId.set(i.attachment.id, i);
  for (const i of incoming) byId.set(i.attachment.id, i);
  return [...byId.values()].sort((a, b) => b.seq - a.seq);   // a stable sort: inside one message the sender's order stays
}

/**
 * The items a thread draws, in order: day pills, the "new messages" divider, and messages marked as the first or last
 * of a run by one sender. Messages still being sent come last.
 * @param lastReadAtOpen what you had read when you opened it: the divider goes before the first message after this that is not yours
 */
export function buildThread(
  messages: readonly ChatMessage[], me: string, lastReadAtOpen: number | null, unreadCount: number, now: Date, pending: readonly Shown[] = [],
): ThreadItem[] {
  const items: ThreadItem[] = [];
  let dividerPlaced = false;
  let previous: { sender: string; at: number; day: string } | null = null;
  let lastDay: string | null = null;

  const add = (m: Shown, key: string): void => {
    const when = new Date(m.createdAt);
    const day = dayKey(when);
    const mine = m.sender.id === me;
    if (lastDay !== day) {
      items.push({ kind: 'day', key: `d:${day}`, label: dayLabel(m.createdAt, now) });
      lastDay = day;
      previous = null;
    }
    if (m.notice) {
      // A notice is a line between messages: it breaks a run, and is never "new" in the sense of the divider.
      items.push({ kind: 'notice', key, msg: m });
      previous = null;
      return;
    }
    if (!dividerPlaced && lastReadAtOpen !== null && unreadCount > 0 && m.seq > lastReadAtOpen && !mine) {
      items.push({ kind: 'new', key: 'new', count: unreadCount });
      dividerPlaced = true;
      previous = null;
    }
    const continues = previous !== null && previous.sender === m.sender.id && when.getTime() - previous.at <= RUN_GAP_MS;
    items.push({ kind: 'msg', key, msg: m, mine, first: !continues, last: true });
    previous = { sender: m.sender.id, at: when.getTime(), day };
  };

  for (const m of messages) add(m, `m:${m.id}`);
  for (const m of pending) add(m, `p:${m.clientId}`);

  // A message is the last of its run unless the next item continues it.
  for (let i = 0; i < items.length - 1; i++) {
    const a = items[i]!; const b = items[i + 1]!;
    if (a.kind === 'msg' && b.kind === 'msg' && !b.first) a.last = false;
  }
  return items;
}

const dayFormat = new Intl.DateTimeFormat(say.locale, { day: '2-digit', month: '2-digit', year: 'numeric' });

/** A notice in words, from the point of view of whoever reads it ("Bạn" and "bạn" for yourself). */
export function noticeText(m: ChatMessage, me: string): string {
  const notice = m.notice;
  if (!notice) return '';
  const who = m.sender.id === me ? say.common.you : m.sender.displayName;
  const target = notice.target ? (notice.target.id === me ? say.notices.you : notice.target.displayName) : '';
  switch (notice.type) {
    case 'added': return say.notices.added(who, target);
    case 'removed': return say.notices.removed(who, target);
    case 'left': return say.notices.left(who);
    case 'renamed': return say.notices.renamed(who, notice.title ?? '');
    // The owner did it, but it is not about anyone: no name.
    case 'filesTrimmed': return say.notices.filesTrimmed(notice.before ? dayFormat.format(new Date(notice.before)) : '');
    case 'messagesTrimmed': return say.notices.messagesTrimmed(notice.before ? dayFormat.format(new Date(notice.before)) : '');
  }
}
