import { beforeEach, describe, expect, it } from 'vitest';
import { STICKERS, recentStickers, rememberSticker, stickerName } from '../Scripts/stickers.js';

describe('stickers', () => {
  beforeEach(() => localStorage.clear());

  it('are 48, each once', () => {
    expect(STICKERS).toHaveLength(48);
    expect(new Set(STICKERS).size).toBe(48);
  });

  it('are named by the emoji they show', () => {
    expect(stickerName('1f602')).toBe('😂');
    expect(stickerName('2764')).toBe('❤');
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
