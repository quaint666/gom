import type { Strings } from './strings/vi.js';
/** A pack: its key (its name is in the string tables) and its stickers. */
export interface StickerPack { key: keyof Strings['stickerPacks']; codes: readonly string[] }

/**
 * The stickers, in packs: still Noto Emoji pictures (Google, Apache License 2.0, from the noto-emoji repository's 2D/svg folder,
 * unchanged), served from /stickers/<code>.svg. The server keeps the same codes (Chat/Stickers.cs), and a server test checks that the two
 * and the files agree. A code never leaves the list once sent: an old message must still draw.
 */
export const STICKER_PACKS: readonly StickerPack[] = [
  { key: 'feelings', codes: [
    '1f600', '1f604', '1f606', '1f605', '1f602', '1f923', '1f60a', '1f607', '1f609', '1f60d', '1f970', '1f618',
    '1f929', '1f60b', '1f61c', '1f92a', '1f917', '1f92d', '1f914', '1f644', '1f60f', '1f60c', '1f634', '1f971',
    '1f60e', '1f973', '1f97a', '1f622', '1f62d', '1f631', '1f633', '1f62c', '1f910', '1f621', '1f624', '1f92f',
    '1f974', '1f975', '1f976', '1f912',
  ] },
  { key: 'gestures', codes: [
    '1f44d', '1f44e', '1f44c', '270c', '1f91e', '1f91f', '1f919', '1f44f', '1f64c', '1f64f', '1f44b', '1f4aa',
    '1f91d', '1faf6',
  ] },
  { key: 'love', codes: [
    '2764', '1f495', '1f496', '1f497', '1f493', '1f498', '1f49d', '1f494', '1f48c', '1f48b', '1f339', '1f490',
  ] },
  { key: 'animals', codes: [
    '1f415', '1f436', '1f408', '1f431', '1f43c', '1f430', '1f43b', '1f437', '1f414', '1f986', '1f41f', '1f422',
    '1f418', '1f98b',
  ] },
  { key: 'food', codes: [
    '1f35c', '1f35a', '1f356', '1f357', '1f354', '1f355', '1f370', '1f369', '1f36d', '1f9cb', '2615', '1f37b',
    '1f349', '1f96d', '1f34a',
  ] },
  { key: 'festivals', codes: [
    '1f389', '1f38a', '1f388', '1f382', '1f381', '1f9e7', '1f9e8', '1f3ee', '1f386', '1f525', '1f31f', '2728',
    '1f3c6', '1f340', '1f4af',
  ] },
  { key: 'weather', codes: [
    '2600', '1f324', '26c5', '1f325', '1f327', '26c8', '1f329', '2744', '2603', '1f308', '2614', '1f32a',
    '1f319', '2b50', '1f30a', '1f338', '1f342', '1f321',
  ] },
  { key: 'travel', codes: [
    '2708', '1f697', '1f3cd', '1f6f5', '1f68c', '1f686', '1f6a2', '1f6b2', '1f3d6', '1f3d4', '1f3d5', '1f5fa',
    '1f9f3', '1f4f8', '1f3e8', '1f5fc',
  ] },
  { key: 'sports', codes: [
    '26bd', '1f3c0', '1f3f8', '1f3d3', '1f3be', '1f3d0', '1f3ca', '1f6b4', '1f3c3', '1f9d8', '1f3cb', '1f947',
    '1f3c5', '1f3ae', '1f3a3', '1f945',
  ] },
  { key: 'school', codes: [
    '1f4da', '270f', '1f4dd', '1f393', '1f4bb', '1f4f1', '23f0', '1f4c5', '1f4bc', '1f4a1', '2705', '274c',
    '2753', '1f4a4', '1f4b0', '1f3e0',
  ] },
  // Every flag the emoji list has (Noto's waved flags; Việt Nam first, then Unicode's order), less the NOT_OFFERED ones.
  { key: 'flags', codes: [
    '1f1fb_1f1f3', '1f3c1', '1f6a9', '1f38c', '1f3f4', '1f3f3', '1f3f3_200d_1f308', '1f3f3_200d_26a7',
    '1f3f4_200d_2620', '1f1e6_1f1e8', '1f1e6_1f1e9', '1f1e6_1f1ea', '1f1e6_1f1eb', '1f1e6_1f1ec', '1f1e6_1f1ee',
    '1f1e6_1f1f1', '1f1e6_1f1f2', '1f1e6_1f1f4', '1f1e6_1f1f6', '1f1e6_1f1f7', '1f1e6_1f1f8', '1f1e6_1f1f9',
    '1f1e6_1f1fa', '1f1e6_1f1fc', '1f1e6_1f1fd', '1f1e6_1f1ff', '1f1e7_1f1e6', '1f1e7_1f1e7', '1f1e7_1f1e9',
    '1f1e7_1f1ea', '1f1e7_1f1eb', '1f1e7_1f1ec', '1f1e7_1f1ed', '1f1e7_1f1ee', '1f1e7_1f1ef', '1f1e7_1f1f2',
    '1f1e7_1f1f3', '1f1e7_1f1f4', '1f1e7_1f1f7', '1f1e7_1f1f8', '1f1e7_1f1f9', '1f1e7_1f1fc', '1f1e7_1f1fe',
    '1f1e7_1f1ff', '1f1e8_1f1e6', '1f1e8_1f1e8', '1f1e8_1f1e9', '1f1e8_1f1eb', '1f1e8_1f1ec', '1f1e8_1f1ed',
    '1f1e8_1f1ee', '1f1e8_1f1f0', '1f1e8_1f1f1', '1f1e8_1f1f2', '1f1e8_1f1f3', '1f1e8_1f1f4', '1f1e8_1f1f7',
    '1f1e8_1f1fa', '1f1e8_1f1fb', '1f1e8_1f1fc', '1f1e8_1f1fd', '1f1e8_1f1fe', '1f1e8_1f1ff', '1f1e9_1f1ea',
    '1f1e9_1f1ef', '1f1e9_1f1f0', '1f1e9_1f1f2', '1f1e9_1f1f4', '1f1e9_1f1ff', '1f1ea_1f1e8', '1f1ea_1f1ea',
    '1f1ea_1f1ec', '1f1ea_1f1ed', '1f1ea_1f1f7', '1f1ea_1f1f8', '1f1ea_1f1f9', '1f1ea_1f1fa', '1f1eb_1f1ee',
    '1f1eb_1f1ef', '1f1eb_1f1f0', '1f1eb_1f1f2', '1f1eb_1f1f4', '1f1eb_1f1f7', '1f1ec_1f1e6', '1f1ec_1f1e7',
    '1f1ec_1f1e9', '1f1ec_1f1ea', '1f1ec_1f1eb', '1f1ec_1f1ec', '1f1ec_1f1ed', '1f1ec_1f1ee', '1f1ec_1f1f1',
    '1f1ec_1f1f2', '1f1ec_1f1f3', '1f1ec_1f1f5', '1f1ec_1f1f6', '1f1ec_1f1f7', '1f1ec_1f1f8', '1f1ec_1f1f9',
    '1f1ec_1f1fa', '1f1ec_1f1fc', '1f1ec_1f1fe', '1f1ed_1f1f0', '1f1ed_1f1f3', '1f1ed_1f1f7', '1f1ed_1f1f9',
    '1f1ed_1f1fa', '1f1ee_1f1e8', '1f1ee_1f1e9', '1f1ee_1f1ea', '1f1ee_1f1f1', '1f1ee_1f1f2', '1f1ee_1f1f3',
    '1f1ee_1f1f4', '1f1ee_1f1f6', '1f1ee_1f1f7', '1f1ee_1f1f8', '1f1ee_1f1f9', '1f1ef_1f1ea', '1f1ef_1f1f2',
    '1f1ef_1f1f4', '1f1ef_1f1f5', '1f1f0_1f1ea', '1f1f0_1f1ec', '1f1f0_1f1ed', '1f1f0_1f1ee', '1f1f0_1f1f2',
    '1f1f0_1f1f3', '1f1f0_1f1f5', '1f1f0_1f1f7', '1f1f0_1f1fc', '1f1f0_1f1fe', '1f1f0_1f1ff', '1f1f1_1f1e6',
    '1f1f1_1f1e7', '1f1f1_1f1e8', '1f1f1_1f1ee', '1f1f1_1f1f0', '1f1f1_1f1f7', '1f1f1_1f1f8', '1f1f1_1f1f9',
    '1f1f1_1f1fa', '1f1f1_1f1fb', '1f1f1_1f1fe', '1f1f2_1f1e6', '1f1f2_1f1e8', '1f1f2_1f1e9', '1f1f2_1f1ea',
    '1f1f2_1f1ec', '1f1f2_1f1ed', '1f1f2_1f1f0', '1f1f2_1f1f1', '1f1f2_1f1f2', '1f1f2_1f1f3', '1f1f2_1f1f4',
    '1f1f2_1f1f5', '1f1f2_1f1f7', '1f1f2_1f1f8', '1f1f2_1f1f9', '1f1f2_1f1fa', '1f1f2_1f1fb', '1f1f2_1f1fc',
    '1f1f2_1f1fd', '1f1f2_1f1fe', '1f1f2_1f1ff', '1f1f3_1f1e6', '1f1f3_1f1e8', '1f1f3_1f1ea', '1f1f3_1f1eb',
    '1f1f3_1f1ec', '1f1f3_1f1ee', '1f1f3_1f1f1', '1f1f3_1f1f4', '1f1f3_1f1f5', '1f1f3_1f1f7', '1f1f3_1f1fa',
    '1f1f3_1f1ff', '1f1f4_1f1f2', '1f1f5_1f1e6', '1f1f5_1f1ea', '1f1f5_1f1eb', '1f1f5_1f1ec', '1f1f5_1f1ed',
    '1f1f5_1f1f0', '1f1f5_1f1f1', '1f1f5_1f1f2', '1f1f5_1f1f3', '1f1f5_1f1f7', '1f1f5_1f1f8', '1f1f5_1f1f9',
    '1f1f5_1f1fc', '1f1f5_1f1fe', '1f1f6_1f1e6', '1f1f7_1f1f4', '1f1f7_1f1f8', '1f1f7_1f1fa', '1f1f7_1f1fc',
    '1f1f8_1f1e6', '1f1f8_1f1e7', '1f1f8_1f1e8', '1f1f8_1f1e9', '1f1f8_1f1ea', '1f1f8_1f1ec', '1f1f8_1f1ed',
    '1f1f8_1f1ee', '1f1f8_1f1f0', '1f1f8_1f1f1', '1f1f8_1f1f2', '1f1f8_1f1f3', '1f1f8_1f1f4', '1f1f8_1f1f7',
    '1f1f8_1f1f8', '1f1f8_1f1f9', '1f1f8_1f1fb', '1f1f8_1f1fd', '1f1f8_1f1fe', '1f1f8_1f1ff', '1f1f9_1f1e6',
    '1f1f9_1f1e8', '1f1f9_1f1e9', '1f1f9_1f1ec', '1f1f9_1f1ed', '1f1f9_1f1ef', '1f1f9_1f1f0', '1f1f9_1f1f1',
    '1f1f9_1f1f2', '1f1f9_1f1f3', '1f1f9_1f1f4', '1f1f9_1f1f7', '1f1f9_1f1f9', '1f1f9_1f1fb', '1f1f9_1f1fc',
    '1f1f9_1f1ff', '1f1fa_1f1e6', '1f1fa_1f1ec', '1f1fa_1f1f3', '1f1fa_1f1f8', '1f1fa_1f1fe', '1f1fa_1f1ff',
    '1f1fb_1f1e6', '1f1fb_1f1e8', '1f1fb_1f1ea', '1f1fb_1f1ec', '1f1fb_1f1ee', '1f1fb_1f1fa', '1f1fc_1f1eb',
    '1f1fc_1f1f8', '1f1fd_1f1f0', '1f1fe_1f1ea', '1f1fe_1f1f9', '1f1ff_1f1e6', '1f1ff_1f1f2', '1f1ff_1f1fc',
    '1f3f4_e0067_e0062_e0065_e006e_e0067_e007f', '1f3f4_e0067_e0062_e0073_e0063_e0074_e007f',
    '1f3f4_e0067_e0062_e0077_e006c_e0073_e007f',
  ] },
];

/**
 * Flags still accepted (one sent before stays a sticker that draws) but no longer offered in the panel. From Noto Emoji's own tables
 * (commit 99701e0): `emoji_aliases.txt` draws the first eight with another region's flag, so they showed twice ("two United States");
 * `unknown_flag_aliases.txt` lists the last five as flags Noto explicitly omits (its font draws an unknown flag), where the pictures here
 * were local or unofficial ones. The server keeps the same list (Chat/Stickers.cs, NotOffered).
 */
export const NOT_OFFERED: readonly string[] = [
  '1f1e7_1f1fb',   // BV: the same picture as NO
  '1f1e8_1f1f5',   // CP: the same picture as FR
  '1f1e9_1f1ec',   // DG: the same picture as IO
  '1f1ea_1f1e6',   // EA: the same picture as ES
  '1f1ed_1f1f2',   // HM: the same picture as AU
  '1f1f2_1f1eb',   // MF: the same picture as FR
  '1f1f8_1f1ef',   // SJ: the same picture as NO
  '1f1fa_1f1f2',   // UM: the same picture as US
  '1f1e7_1f1f1',   // BL
  '1f1e7_1f1f6',   // BQ
  '1f1f2_1f1f6',   // MQ
  '1f1f7_1f1ea',   // RE
  '1f1f9_1f1eb',   // TF
];

/** Every sticker, pack after pack. */
export const STICKERS: readonly string[] = STICKER_PACKS.flatMap((p) => p.codes);

/** One of the owner's stickers (Chat/StickerPacks.cs): "c-" and its id, its picture from the server. */
export const isCustomSticker = (code: string): boolean => /^c-[0-9a-f]{32}$/.test(code);

export const stickerUrl = (code: string): string => isCustomSticker(code) ? `/api/stickers/${code.slice(2)}` : `/stickers/${code}.svg`;

/** The emoji a sticker shows, for a screen reader ("Gửi nhãn dán 😂"). */
export const stickerName = (code: string): string => isCustomSticker(code) ? '' : String.fromCodePoint(...code.split('_').map((p) => parseInt(p, 16)));

/** The stickers this device sent last, newest first, kept in this browser's storage. */
const RECENT_KEY = 'gom-stickers-recent';
const RECENT_MAX = 8;

export function recentStickers(): string[] {
  try {
    const saved: unknown = JSON.parse(localStorage.getItem(RECENT_KEY) ?? '[]');
    return Array.isArray(saved) ? saved.filter((c): c is string => typeof c === 'string' && (STICKERS.includes(c) || isCustomSticker(c))).slice(0, RECENT_MAX) : [];
  } catch { return []; }
}

export function rememberSticker(code: string): void {
  try { localStorage.setItem(RECENT_KEY, JSON.stringify([code, ...recentStickers().filter((c) => c !== code)].slice(0, RECENT_MAX))); } catch { /* just not remembered */ }
}
