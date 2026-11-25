import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createApi } from '../Scripts/api.js';
import { mountSettings } from '../Scripts/pages/settings.js';
import { chosenLanguage, pageTitle } from '../Scripts/say.js';
import { en } from '../Scripts/strings/en.js';
import { vi as vietnamese } from '../Scripts/strings/vi.js';
import { fail, fakeApi, flush, ok } from './helpers.js';

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

  it('count in English: one, or many', () => {
    expect(en.common.members(1)).toBe('1 member');
    expect(en.common.members(2)).toBe('2 members');
    expect(en.chat.newMessages(1)).toBe('1 new message');
    expect(en.storage.counted(1, '1 MB', 2, '2 MB')).toBe('1 picture (1 MB) and 2 files (2 MB)');
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
