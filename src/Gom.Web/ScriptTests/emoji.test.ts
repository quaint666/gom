import { beforeEach, describe, expect, it } from 'vitest';
import { EMOJI_GROUPS, emojiOf, onlyEmoji, recentEmoji, rememberEmoji } from '../Scripts/emoji.js';

describe('emoji', () => {
  beforeEach(() => localStorage.clear());

  it("come in Unicode's nine groups, without skin tones, each once", () => {
    expect(EMOJI_GROUPS.map((g) => g.name)).toEqual(['Mặt cười', 'Con người', 'Động vật và thiên nhiên', 'Đồ ăn và thức uống', 'Du lịch và địa điểm', 'Hoạt động', 'Đồ vật', 'Biểu tượng', 'Cờ']);
    const all = EMOJI_GROUPS.flatMap((_, i) => emojiOf(i));
    expect(all).toHaveLength(1849);
    expect(new Set(all).size).toBe(all.length);
    expect(all.some((e) => /[\u{1F3FB}-\u{1F3FF}]/u.test(e))).toBe(false);
    expect(emojiOf(0)[0]).toBe('😀');
  });

  it('tell a message of one to three emoji from anything else', () => {
    for (const yes of ['😂', ' 😂 ', '❤️', '😂😂😂', '😂 ❤️ 👍', '🇻🇳', '👨‍👩‍👧', '👍🏽']) expect(onlyEmoji(yes), yes).toBe(true);
    for (const no of ['', '   ', 'a', '😂😂😂😂', 'ha 😂', '123', '1️⃣', '😂!', ':)']) expect(onlyEmoji(no), no).toBe(false);
  });

  it('remember the ones used lately, newest first, and ignore anything else in storage', () => {
    rememberEmoji('😀'); rememberEmoji('😂'); rememberEmoji('😀');
    expect(recentEmoji()).toEqual(['😀', '😂']);
    for (const e of emojiOf(0).slice(0, 30)) rememberEmoji(e);
    expect(recentEmoji()).toHaveLength(24);
    localStorage.setItem('gom-emoji-recent', JSON.stringify(['😀', '<b>', 7]));
    expect(recentEmoji()).toEqual(['😀']);
    localStorage.setItem('gom-emoji-recent', 'nope');
    expect(recentEmoji()).toEqual([]);
  });
});
