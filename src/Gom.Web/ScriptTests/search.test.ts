import { describe, expect, it } from 'vitest';
import { markWords, markedExcerpt, matches, searchWords } from '../Scripts/search.js';

const shown = (runs: { text: string; hit: boolean }[]) => runs.map((r) => (r.hit ? `[${r.text}]` : r.text)).join('');

describe('search words', () => {
  it('folds each word and drops the spaces', () => {
    expect(searchWords('  Phở   HÀ nội ')).toEqual(['pho', 'ha', 'noi']);
    expect(searchWords('   ')).toEqual([]);
  });

  it('matches every word in any order', () => {
    expect(matches('Phở ở Hà Nội', 'noi pho')).toBe(true);
    expect(matches('Phở ở Hà Nội', 'pho hue')).toBe(false);
  });
});

describe('marking the words found', () => {
  it('marks every place each word appears, whatever its accents and capitals, and keeps the text exactly', () => {
    const runs = markWords('Phở ở Hà Nội, phở bò', 'pho noi');
    expect(shown(runs)).toBe('[Phở] ở Hà [Nội], [phở] bò');
    expect(runs.map((r) => r.text).join('')).toBe('Phở ở Hà Nội, phở bò');
  });

  it('marks đ as d and keeps an accent written on its own with its letter', () => {
    expect(shown(markWords('Đình', 'dinh'))).toBe('[Đình]');
    expect(shown(markWords('Phở bò', 'pho'))).toBe('[Phở] bò');
  });

  it('joins words found side by side into one mark, and marks nothing for an empty search', () => {
    expect(shown(markWords('ăn phở', 'an pho'))).toBe('[ăn] [phở]');
    expect(shown(markWords('anpho', 'an pho'))).toBe('[anpho]');
    expect(markWords('ăn phở', '')).toEqual([{ text: 'ăn phở', hit: false }]);
  });
});

describe('an excerpt of a long message', () => {
  it('keeps a short message whole', () => {
    expect(shown(markedExcerpt('ăn phở nhé', 'pho', 50))).toBe('ăn [phở] nhé');
  });

  it('cuts a long one around the first word found, with … where something was left out', () => {
    const long = `${'a '.repeat(100)}phở ${'b '.repeat(100)}`;
    const text = shown(markedExcerpt(long, 'pho', 60));
    expect(text.startsWith('…')).toBe(true);
    expect(text.endsWith('…')).toBe(true);
    expect(text).toContain('[phở]');
    expect([...text.replace(/[[\]]/g, '')].length).toBe(62);   // 60 characters and two …
  });

  it('starts at the beginning when the word is near it, and ends at the end when the word is near that', () => {
    expect(shown(markedExcerpt(`phở ${'a '.repeat(100)}`, 'pho', 40)).startsWith('[phở]')).toBe(true);
    const tail = shown(markedExcerpt(`${'a '.repeat(100)}cuối phở`, 'pho', 40));
    expect(tail.endsWith('[phở]')).toBe(true);
    expect(tail.startsWith('…')).toBe(true);
  });

  it('never splits an emoji', () => {
    const text = shown(markedExcerpt(`${'👨‍👩‍👧 '.repeat(40)}phở`, 'pho', 10));
    expect(text).not.toMatch(/‍…|…‍/);
    expect(text).toContain('[phở]');
  });
});
