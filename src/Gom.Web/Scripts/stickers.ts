/**
 * The stickers: still Noto Emoji pictures (Google, SIL Open Font License 1.1), served from /stickers/<code>.svg. This is the order
 * the panel shows them in; the server keeps the same list (Chat/Stickers.cs), and a server test checks that the two and the files agree.
 */
export const STICKERS: readonly string[] = [
  '1f600', '1f604', '1f606', '1f605', '1f602', '1f923', '1f60a', '1f607', '1f609', '1f60d', '1f970', '1f618',
  '1f60b', '1f61c', '1f92a', '1f917', '1f92d', '1f914', '1f644', '1f60f', '1f634', '1f60e', '1f973', '1f97a',
  '1f622', '1f62d', '1f631', '1f633', '1f621', '1f624', '1f92f', '1f912', '1f44d', '1f44f', '1f64f', '1f44b',
  '1f4aa', '1f91d', '2764', '1f494', '1f525', '1f389', '1f382', '1f381', '1f339', '1f4af', '1f415', '1f98b',
];

export const stickerUrl = (code: string): string => `/stickers/${code}.svg`;

/** The emoji a sticker shows, for a screen reader ("Gửi nhãn dán 😂"). */
export const stickerName = (code: string): string => String.fromCodePoint(...code.split('_').map((p) => parseInt(p, 16)));

/** The stickers this device sent last, newest first, kept in this browser's storage. */
const RECENT_KEY = 'gom-stickers-recent';
const RECENT_MAX = 8;

export function recentStickers(): string[] {
  try {
    const saved: unknown = JSON.parse(localStorage.getItem(RECENT_KEY) ?? '[]');
    return Array.isArray(saved) ? saved.filter((c): c is string => typeof c === 'string' && STICKERS.includes(c)).slice(0, RECENT_MAX) : [];
  } catch { return []; }
}

export function rememberSticker(code: string): void {
  try { localStorage.setItem(RECENT_KEY, JSON.stringify([code, ...recentStickers().filter((c) => c !== code)].slice(0, RECENT_MAX))); } catch { /* just not remembered */ }
}
