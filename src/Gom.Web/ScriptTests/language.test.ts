import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createApi } from '../Scripts/api.js';
import { mountSettings } from '../Scripts/pages/settings.js';
import { chosenLanguage, pageTitle } from '../Scripts/say.js';
import { en } from '../Scripts/strings/en.js';
import { vi as vietnamese } from '../Scripts/strings/vi.js';
import { fail, fakeApi, flush, ok, text } from './helpers.js';

let root: HTMLElement;
beforeEach(() => { root = document.createElement('div'); document.body.replaceChildren(root); });

describe('choosing the language', () => {
  it('follows the choice this device remembers, else English for an English browser, else Vietnamese', () => {
    expect(chosenLanguage(() => 'en', 'vi-VN')).toBe('en');
    expect(chosenLanguage(() => 'vi', 'en-US')).toBe('vi');
    expect(chosenLanguage(() => null, 'en-GB')).toBe('en');
    expect(chosenLanguage(() => null, 'fr-FR')).toBe('vi');
    expect(chosenLanguage(() => 'xx', 'en')).toBe('en');
    expect(chosenLanguage(() => { throw new Error('storage off'); }, 'vi')).toBe('vi');
  });

  it('titles a page in its language, and anything unknown is just Gom', () => {
    expect(pageTitle('settings', vietnamese)).toBe('Cài đặt · Gom');
    expect(pageTitle('settings', en)).toBe('Settings · Gom');
    expect(pageTitle('home', en)).toBe('Gom');
  });
});

describe('the English words', () => {
  /** Every entry of a table, by its path, with a sample filled in where it takes something. */
  const flatten = (table: object, prefix = ''): Array<[string, string]> => Object.entries(table).flatMap(([key, value]) =>
    typeof value === 'object' ? flatten(value as object, `${prefix}${key}.`)
      : [[`${prefix}${key}`, typeof value === 'function' ? String((value as (...a: unknown[]) => unknown)(2, 3, 4, 5)) : String(value)]]);

  it('have every line the Vietnamese have, none empty', () => {
    const vn = new Map(flatten(vietnamese));
    const english = new Map(flatten(en));
    expect([...english.keys()].sort()).toEqual([...vn.keys()].sort());
    for (const [key, text] of english) expect(text.trim(), key).not.toBe('');
  });

  it('say who is typing in English, with "is" for one and "are" for more, and count the rest', () => {
    expect(en.chat.typing('Bảo', 1, 0)).toBe('Bảo is typing…');
    expect(en.chat.typing('Bảo, Mai', 2, 0)).toBe('Bảo, Mai are typing…');
    expect(en.chat.typing('Bảo, Mai, Lan', 3, 1)).toBe('Bảo, Mai, Lan and 1 other are typing…');
    expect(en.chat.typing('Bảo, Mai, Lan', 3, 2)).toBe('Bảo, Mai, Lan and 2 others are typing…');
    expect(en.chat.typingDirect).toBe('typing…');
  });

  it('count in English: one, or many', () => {
    expect(en.common.members(1)).toBe('1 member');
    expect(en.common.members(2)).toBe('2 members');
    expect(en.chat.newMessages(1)).toBe('1 new message');
    expect(en.storage.counted(1, '1 MB', 1, '5 MB', 2, '2 MB')).toBe('1 picture (1 MB), 1 video (5 MB) and 2 files (2 MB)');
  });
});

describe('settings: language', () => {
  const me = { id: 'u1', gomId: 'mom', displayName: 'Mẹ', isOwner: false, language: 'vi' as const };

  it('saves English with the account, remembers it on this device and opens the page again', async () => {
    const go = vi.fn();
    const a = fakeApi({ me: vi.fn(async () => ok(me)), sessions: vi.fn(async () => ok([])), pushState: vi.fn(async () => fail('unknown', 404)) });
    mountSettings(root, a, go);
    await flush();
    expect(root.querySelector<HTMLInputElement>('#language-vi')!.checked).toBe(true);
    root.querySelector<HTMLInputElement>('#language-en')!.dispatchEvent(new Event('change'));
    await flush();
    expect(a.setLanguage).toHaveBeenCalledWith('en');
    expect(localStorage.getItem('gom-language')).toBe('en');
    expect(go).toHaveBeenCalledWith('/settings');
    localStorage.setItem('gom-language', 'vi');
  });
});

describe('settings: colour', () => {
  const me = { id: 'u1', gomId: 'mom', displayName: 'Mẹ', isOwner: false, language: 'vi' as const };
  const swatch = (code: string) => root.querySelector<HTMLInputElement>(`[data-palette-choice] input[value="${code}"]`)!;

  it('shows the sixteen, draws a chosen one at once and saves it; a refusal puts the old one back with the reason', async () => {
    localStorage.clear();
    delete document.documentElement.dataset['palette'];
    const setPalette = vi.fn().mockResolvedValueOnce(ok(undefined)).mockResolvedValueOnce(fail('invalid_palette', 400));
    const a = fakeApi({ me: vi.fn(async () => ok(me)), sessions: vi.fn(async () => ok([])), pushState: vi.fn(async () => fail('unknown', 404)), setPalette });
    mountSettings(root, a, vi.fn());
    await flush();
    expect(root.querySelectorAll('[data-palette-choice] input')).toHaveLength(16);
    expect(swatch('sage').checked).toBe(true);
    expect(text(root.querySelector('[data-palette-choice]')!)).toContain('Oải hương');

    swatch('jade').checked = true;
    swatch('jade').dispatchEvent(new Event('change'));
    expect(document.documentElement.dataset['palette']).toBe('jade');
    await flush();
    expect(setPalette).toHaveBeenCalledWith('jade');
    expect(localStorage.getItem('gom-palette')).toBe('jade');

    swatch('moss').checked = true;
    swatch('moss').dispatchEvent(new Event('change'));
    await flush();
    expect(document.documentElement.dataset['palette']).toBe('jade');
    expect(swatch('jade').checked).toBe(true);
    expect(text(root)).toContain('Gom chưa có màu này.');
    localStorage.clear();
    delete document.documentElement.dataset['palette'];
  });
});

describe('settings: pure black', () => {
  it('turns it on for this device without asking the server, and off again', async () => {
    localStorage.clear();
    delete document.documentElement.dataset['black'];
    const a = fakeApi({ me: vi.fn(async () => ok({ id: 'u1', gomId: 'mom', displayName: 'Mẹ', isOwner: false })), sessions: vi.fn(async () => ok([])), pushState: vi.fn(async () => fail('unknown', 404)) });
    mountSettings(root, a, vi.fn());
    await flush();
    const box = root.querySelector<HTMLInputElement>('[data-black-switch]')!;
    expect(box.checked).toBe(false);
    box.checked = true;
    box.dispatchEvent(new Event('change'));
    expect(document.documentElement.hasAttribute('data-black')).toBe(true);
    expect(localStorage.getItem('gom-black')).toBe('1');
    box.checked = false;
    box.dispatchEvent(new Event('change'));
    expect(document.documentElement.hasAttribute('data-black')).toBe(false);
    expect(localStorage.getItem('gom-black')).toBeNull();
  });
});

describe('settings: background', () => {
  const me = { id: 'u1', gomId: 'mom', displayName: 'Mẹ', isOwner: false, language: 'vi' as const };
  const tile = (code: string) => root.querySelector<HTMLInputElement>(`[data-pattern-choice] input[value="${code}"]`)!;

  it('shows the nine, each drawn as the conversation would, remembers a chosen one and saves it; a refusal puts the old one back', async () => {
    localStorage.clear();
    const setPattern = vi.fn().mockResolvedValueOnce(ok(undefined)).mockResolvedValueOnce(fail('invalid_pattern', 400));
    const a = fakeApi({ me: vi.fn(async () => ok(me)), sessions: vi.fn(async () => ok([])), pushState: vi.fn(async () => fail('unknown', 404)), setPattern });
    mountSettings(root, a, vi.fn());
    await flush();
    expect(root.querySelectorAll('[data-pattern-choice] input')).toHaveLength(15);
    expect(tile('plain').checked).toBe(true);
    expect(root.querySelector('[data-pattern-choice] .backdrop[data-pattern="tiles"]')).not.toBeNull();
    expect(text(root.querySelector('[data-pattern-choice]')!)).toContain('Gạch bông');

    tile('clouds').checked = true;
    tile('clouds').dispatchEvent(new Event('change'));
    expect(localStorage.getItem('gom-pattern')).toBe('clouds');
    await flush();
    expect(setPattern).toHaveBeenCalledWith('clouds');

    tile('grid').checked = true;
    tile('grid').dispatchEvent(new Event('change'));
    await flush();
    expect(localStorage.getItem('gom-pattern')).toBe('clouds');
    expect(tile('clouds').checked).toBe(true);
    expect(text(root)).toContain('Gom chưa có hình nền này.');
    localStorage.clear();
  });
});

describe('the API: the person\'s colour', () => {
  it('tells the page the account\'s colour', async () => {
    const onPalette = vi.fn();
    const api = createApi(async () => new Response(JSON.stringify({ id: 'u1', gomId: 'mom', displayName: 'Mẹ', isOwner: false, palette: 'sea' })), undefined, { onPalette });
    await api.me();
    expect(onPalette).toHaveBeenCalledWith('sea');
  });
});

describe('the API: the person\'s language', () => {
  it('tells the page when the account\'s language is not the page\'s', async () => {
    const onLanguage = vi.fn();
    const answer = (language: string) => new Response(JSON.stringify({ id: 'u1', gomId: 'mom', displayName: 'Mẹ', isOwner: false, language }));
    const answers = [answer('vi'), answer('en')];
    const api = createApi(async () => answers.shift()!, undefined, { onLanguage });
    await api.me();
    expect(onLanguage).not.toHaveBeenCalled();
    await api.me();
    expect(onLanguage).toHaveBeenCalledWith('en');
  });
});

describe('counts of unread messages', () => {
  it('say "99+" past 99, as the badge does: the server counts no further than 100', () => {
    expect(vietnamese.chat.unreadCount(7)).toBe('7 tin chưa đọc');
    expect(vietnamese.chat.unreadCount(100)).toBe('99+ tin chưa đọc');
    expect(vietnamese.chat.newMessages(100)).toBe('99+ tin nhắn mới');
    expect(en.chat.unreadCount(1)).toBe('1 unread message');
    expect(en.chat.unreadCount(100)).toBe('99+ unread messages');
    expect(en.chat.newMessages(100)).toBe('99+ new messages');
  });
});
