import type { ChatMessage, Failure } from '../api.js';

/** A message as the thread shows it: stored, or still on its way (no sequence number yet). */
export type Shown = ChatMessage & { state?: 'sending' | 'queued' | 'failed'; error?: Failure };

export type ThreadItem =
  | { kind: 'day'; key: string; label: string }
  | { kind: 'new'; key: string; count: number }
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
  if (sameDay(d, now)) return 'Hôm nay';
  if (sameDay(d, yesterdayOf(now))) return 'Hôm qua';
  return `${pad(d.getDate())}/${pad(d.getMonth() + 1)}/${d.getFullYear()}`;
}

/** The short time in a list row: a clock today, then "Hôm qua", then the date. */
export function listTime(iso: string, now: Date): string {
  const d = new Date(iso);
  if (sameDay(d, now)) return clock(iso);
  if (sameDay(d, yesterdayOf(now))) return 'Hôm qua';
  return d.getFullYear() === now.getFullYear() ? `${d.getDate()}/${d.getMonth() + 1}` : `${d.getDate()}/${d.getMonth() + 1}/${d.getFullYear()}`;
}

/** Both lists in sequence order, no repeats; a later copy of the same message wins. Neither input is changed. */
export function mergeMessages(have: readonly ChatMessage[], incoming: readonly ChatMessage[]): ChatMessage[] {
  const bySeq = new Map<number, ChatMessage>();
  for (const m of have) bySeq.set(m.seq, m);
  for (const m of incoming) bySeq.set(m.seq, m);
  return [...bySeq.values()].sort((a, b) => a.seq - b.seq);
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

  const add = (m: Shown, key: string): void => {
    const when = new Date(m.createdAt);
    const day = dayKey(when);
    const mine = m.sender.gomId === me;
    if (previous === null || previous.day !== day) {
      items.push({ kind: 'day', key: `d:${day}`, label: dayLabel(m.createdAt, now) });
      previous = null;
    }
    if (!dividerPlaced && lastReadAtOpen !== null && unreadCount > 0 && m.seq > lastReadAtOpen && !mine) {
      items.push({ kind: 'new', key: 'new', count: unreadCount });
      dividerPlaced = true;
      previous = null;
    }
    const continues = previous !== null && previous.sender === m.sender.gomId && when.getTime() - previous.at <= RUN_GAP_MS;
    items.push({ kind: 'msg', key, msg: m, mine, first: !continues, last: true });
    previous = { sender: m.sender.gomId, at: when.getTime(), day };
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
