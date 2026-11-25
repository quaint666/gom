import { en } from './strings/en.js';
import { vi, type Strings } from './strings/vi.js';

export type Language = 'vi' | 'en';
/** Each language named in itself, for the choice in Settings. */
export const LANGUAGES: ReadonlyArray<readonly [Language, string]> = [['vi', 'Tiếng Việt'], ['en', 'English']];

const KEY = 'gom-language';

/**
 * The language of this page: the person's choice as this device last heard it (Settings, or their account on signing in); with
 * none, English for a browser set to English, else Vietnamese.
 */
export function chosenLanguage(stored: () => string | null = () => localStorage.getItem(KEY), browser = navigator.language): Language {
  try { const s = stored(); if (s === 'vi' || s === 'en') return s; } catch { /* storage off: fall through */ }
  return browser.toLowerCase().startsWith('en') ? 'en' : 'vi';
}

/** Remembers the language for the next page this device opens. */
export function rememberLanguage(language: Language): void {
  try { localStorage.setItem(KEY, language); } catch { /* not remembered: the account's choice still wins on the next sign-in */ }
}

export const language: Language = chosenLanguage();

/** The words the pages use (`say.chat.offline`), in this page's language. */
export const say: Strings = language === 'en' ? en : vi;

/** A page's title: "Cài đặt · Gom", or just "Gom". */
export function pageTitle(page: string, words: Strings = say): string {
  const title = (words.titles as Record<string, string | undefined>)[page];
  return title === undefined ? 'Gom' : `${title} · Gom`;
}
