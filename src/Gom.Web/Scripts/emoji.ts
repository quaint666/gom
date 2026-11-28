/** The emoji tab of the panel under the composer, and the large drawing of a message that is only a few emoji. */
import { EMOJI_GROUPS } from './emoji-data.js';

export { EMOJI_GROUPS };

/** The emoji of one group, in Unicode's order. */
export const emojiOf = (group: number): string[] => EMOJI_GROUPS[group]?.list.split(' ') ?? [];

const ALL = new Set(EMOJI_GROUPS.flatMap((g) => g.list.split(' ')));

/** The emoji this device put in a message last, newest first, kept in this browser's storage. */
const RECENT_KEY = 'gom-emoji-recent';
const RECENT_MAX = 24;

export function recentEmoji(): string[] {
  try {
    const saved: unknown = JSON.parse(localStorage.getItem(RECENT_KEY) ?? '[]');
    return Array.isArray(saved) ? saved.filter((e): e is string => typeof e === 'string' && ALL.has(e)).slice(0, RECENT_MAX) : [];
  } catch { return []; }
}

export function rememberEmoji(emoji: string): void {
  try { localStorage.setItem(RECENT_KEY, JSON.stringify([emoji, ...recentEmoji().filter((e) => e !== emoji)].slice(0, RECENT_MAX))); } catch { /* just not remembered */ }
}

/** A message this many emoji or fewer, and nothing else, is drawn large without a bubble. */
const JUMBO_MAX = 3;
const PICTURE = /\p{Extended_Pictographic}|\p{Regional_Indicator}/u;
/** Made once: making one is slow, and every message on screen is asked about at every drawing. */
const GRAPHEMES = new Intl.Segmenter(undefined, { granularity: 'grapheme' });

/** Whether these words are only one to three emoji (spaces between them allowed). */
export function onlyEmoji(body: string): boolean {
  const text = body.replace(/\s+/gu, '');
  if (text === '') return false;
  const parts = [...GRAPHEMES.segment(text)].map((s) => s.segment);
  return parts.length <= JUMBO_MAX && parts.every((p) => PICTURE.test(p));
}
