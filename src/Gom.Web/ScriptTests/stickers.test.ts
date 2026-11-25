import { beforeEach, describe, expect, it } from 'vitest';
import { NOT_OFFERED, STICKER_PACKS, STICKERS, recentStickers, rememberSticker, stickerName } from '../Scripts/stickers.js';
import { vi } from '../Scripts/strings/vi.js';

describe('stickers', () => {
  beforeEach(() => localStorage.clear());

  it('are 432 in eleven named packs, each once, with the flags last', () => {
    expect(STICKERS).toHaveLength(432);
    expect(new Set(STICKERS).size).toBe(432);
    expect(STICKER_PACKS.map((p) => vi.stickerPacks[p.key])).toEqual(['Cảm xúc', 'Cử chỉ', 'Yêu thương', 'Động vật', 'Đồ ăn', 'Lễ hội', 'Thời tiết', 'Du lịch', 'Thể thao', 'Học tập', 'Cờ']);
    for (const p of STICKER_PACKS) expect(p.codes.length, p.key).toBeGreaterThan(0);
  });

  it('still has every sticker that was ever offered, so a message already sent still draws', () => {
    const first48 = ['1f600', '1f604', '1f606', '1f605', '1f602', '1f923', '1f60a', '1f607', '1f609', '1f60d', '1f970', '1f618',
      '1f60b', '1f61c', '1f92a', '1f917', '1f92d', '1f914', '1f644', '1f60f', '1f634', '1f60e', '1f973', '1f97a',
      '1f622', '1f62d', '1f631', '1f633', '1f621', '1f624', '1f92f', '1f912', '1f44d', '1f44f', '1f64f', '1f44b',
      '1f4aa', '1f91d', '2764', '1f494', '1f525', '1f389', '1f382', '1f381', '1f339', '1f4af', '1f415', '1f98b'];
    for (const code of first48) expect(STICKERS, code).toContain(code);
  });

  it('are named by the emoji they show', () => {
    expect(stickerName('1f602')).toBe('😂');
    expect(stickerName('2764')).toBe('❤');
    expect(stickerName('1f1fb_1f1f3')).toBe('🇻🇳');
  });

  it('have every flag of the emoji list but the thirteen not offered, Việt Nam first, and England\'s fits the database column (48)', () => {
    const flags = STICKER_PACKS.find((p) => p.key === 'flags')!.codes;
    expect(flags).toHaveLength(256);
    expect(NOT_OFFERED).toHaveLength(13);
    for (const code of NOT_OFFERED) expect(STICKERS, code).not.toContain(code);
    expect(flags).toContain('1f1fa_1f1f8');      // the United States, once
    expect(flags).not.toContain('1f1fa_1f1f2');  // the U.S. Outlying Islands, the same picture
    expect(flags[0]).toBe('1f1fb_1f1f3');
    expect(flags).toContain('1f3f4_e0067_e0062_e0065_e006e_e0067_e007f');
    expect(Math.max(...STICKERS.map((c) => c.length))).toBeLessThanOrEqual(48);
  });

  it('forget a sticker no longer offered from "Gần đây"', () => {
    rememberSticker('1f1fa_1f1f2');
    rememberSticker('1f1fa_1f1f8');
    expect(recentStickers()).toEqual(['1f1fa_1f1f8']);
  });

  it('remember at most 8 sent last, newest first, and ignore anything else found in storage', () => {
    for (const code of STICKERS.slice(0, 10)) rememberSticker(code);
    expect(recentStickers()).toEqual(STICKERS.slice(2, 10).reverse());
    localStorage.setItem('gom-stickers-recent', JSON.stringify(['1f602', 'evil', 5, '../x']));
    expect(recentStickers()).toEqual(['1f602']);
    localStorage.setItem('gom-stickers-recent', '{not json');
    expect(recentStickers()).toEqual([]);
  });
});
