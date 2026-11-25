/** A pack of stickers: a name for its button, and its stickers in the order the panel shows them. */
export interface StickerPack { name: string; codes: readonly string[] }

/**
 * The stickers, in packs: still Noto Emoji pictures (Google, SIL Open Font License 1.1, from the noto-emoji repository's 2D/svg folder,
 * unchanged), served from /stickers/<code>.svg. The server keeps the same codes (Chat/Stickers.cs), and a server test checks that the two
 * and the files agree. A code never leaves the list once sent: an old message must still draw.
 */
export const STICKER_PACKS: readonly StickerPack[] = [
  { name: 'Cảm xúc', codes: [
    '1f600', '1f604', '1f606', '1f605', '1f602', '1f923', '1f60a', '1f607', '1f609', '1f60d', '1f970', '1f618',
    '1f929', '1f60b', '1f61c', '1f92a', '1f917', '1f92d', '1f914', '1f644', '1f60f', '1f60c', '1f634', '1f971',
    '1f60e', '1f973', '1f97a', '1f622', '1f62d', '1f631', '1f633', '1f62c', '1f910', '1f621', '1f624', '1f92f',
    '1f974', '1f975', '1f976', '1f912',
  ] },
  { name: 'Cử chỉ', codes: [
    '1f44d', '1f44e', '1f44c', '270c', '1f91e', '1f91f', '1f919', '1f44f', '1f64c', '1f64f', '1f44b', '1f4aa',
    '1f91d', '1faf6',
  ] },
  { name: 'Yêu thương', codes: [
    '2764', '1f495', '1f496', '1f497', '1f493', '1f498', '1f49d', '1f494', '1f48c', '1f48b', '1f339', '1f490',
  ] },
  { name: 'Động vật', codes: [
    '1f415', '1f436', '1f408', '1f431', '1f43c', '1f430', '1f43b', '1f437', '1f414', '1f986', '1f41f', '1f422',
    '1f418', '1f98b',
  ] },
  { name: 'Đồ ăn', codes: [
    '1f35c', '1f35a', '1f356', '1f357', '1f354', '1f355', '1f370', '1f369', '1f36d', '1f9cb', '2615', '1f37b',
    '1f349', '1f96d', '1f34a',
  ] },
  { name: 'Lễ hội', codes: [
    '1f389', '1f38a', '1f388', '1f382', '1f381', '1f9e7', '1f9e8', '1f3ee', '1f386', '1f525', '1f31f', '2728',
    '1f3c6', '1f340', '1f4af',
  ] },
];

/** Every sticker, pack after pack. */
export const STICKERS: readonly string[] = STICKER_PACKS.flatMap((p) => p.codes);

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
