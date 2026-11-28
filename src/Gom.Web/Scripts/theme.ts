export type Scheme = 'light' | 'dark';
const KEY = 'color-scheme';

export function applyScheme(override: Scheme | null): void {
  const meta = document.querySelector('meta[name="color-scheme"]');
  meta?.setAttribute('content', override ?? 'light dark');
  try {
    if (override === null) localStorage.removeItem(KEY); else localStorage.setItem(KEY, override);
  } catch { /* site data blocked: the scheme still applies for this visit */ }
}

export function storedScheme(): Scheme | null {
  try {
    const value = localStorage.getItem(KEY);
    return value === 'light' || value === 'dark' ? value : null;
  } catch { return null; }
}

/** What the person chose: follow the system, or light, or dark. */
export type Choice = 'system' | Scheme;
export const chosenScheme = (): Choice => storedScheme() ?? 'system';
export const chooseScheme = (choice: Choice): void => applyScheme(choice === 'system' ? null : choice);

/** The colours a person can choose, in the order Settings shows them. The codes are Chat/Appearance.cs's (a server test checks). */
export const PALETTES = ['sage', 'sea', 'lotus', 'turmeric', 'lavender', 'graphite', 'jade', 'indigo', 'plum', 'lacquer', 'coffee', 'moss', 'clay', 'sky', 'wine', 'rice',
  'sunset', 'nightsea', 'forest', 'blossom', 'ember', 'candy', 'aurora', 'lime'] as const;
/** The last eight are gradients (`--mine` in Styles/00-tokens.css): Settings shows them on their own, as "Chuyển màu". */
export const GRADIENTS: readonly Palette[] = PALETTES.slice(16);
export type Palette = typeof PALETTES[number];
export const isPalette = (value: unknown): value is Palette => typeof value === 'string' && (PALETTES as readonly string[]).includes(value);

const PALETTE_KEY = 'gom-palette';

/** The colour this device last heard of (Settings, or the account on the last page); sage until then. */
export function storedPalette(): Palette {
  try { const value = localStorage.getItem(PALETTE_KEY); if (isPalette(value)) return value; } catch { /* storage off */ }
  return 'sage';
}

/**
 * Draws the pages in this colour from now on, remembers it for the next page (theme-init.js reads it before the first paint), and
 * gives the browser's bar the same colour.
 */
export function applyPalette(palette: Palette, root: HTMLElement = document.documentElement): void {
  if (palette === 'sage') delete root.dataset['palette']; else root.dataset['palette'] = palette;
  try { localStorage.setItem(PALETTE_KEY, palette); } catch { /* remembered for this page only */ }
  syncThemeColor(root);
}

/** The browser's bar takes the page's main colour, as the stylesheet resolves it, so the colour is written in one place. */
export function syncThemeColor(root: HTMLElement = document.documentElement): void {
  const colour = getComputedStyle(root).getPropertyValue('--btn').trim();
  if (colour !== '') document.querySelector('meta[name="theme-color"]')?.setAttribute('content', colour);
}

/** For the code that changes it, and for another tab that hears of the change. */
export const PALETTE_STORAGE_KEY = PALETTE_KEY;

/** The backgrounds behind the messages, in the order Settings shows them; "plain" has none. The codes are Chat/Appearance.cs's. */
export const PATTERNS = ['plain', 'dots', 'tiles', 'waves', 'bamboo', 'clouds', 'grid', 'weave', 'lotus', 'nonla', 'drum', 'blossom', 'lantern', 'koi', 'stars'] as const;
export type Pattern = typeof PATTERNS[number];
export const isPattern = (value: unknown): value is Pattern => typeof value === 'string' && (PATTERNS as readonly string[]).includes(value);

const PATTERN_KEY = 'gom-pattern';

/** The background this device last heard of; plain until then. */
export function storedPattern(): Pattern {
  try { const value = localStorage.getItem(PATTERN_KEY); if (isPattern(value)) return value; } catch { /* storage off */ }
  return 'plain';
}

/** Remembers the background for this device's pages (the chat page draws it from here). */
export function rememberPattern(pattern: Pattern): void {
  try { localStorage.setItem(PATTERN_KEY, pattern); } catch { /* remembered for this page only */ }
}

const BLACK_KEY = 'gom-black';

/** Whether this device draws dark pages on pure black (for OLED screens). Per device, like light and dark: it is about the screen. */
export function storedBlack(): boolean {
  try { return localStorage.getItem(BLACK_KEY) === '1'; } catch { return false; }
}

/** Turns pure black on or off for this device's pages, now and from the first paint of the next (theme-init.js reads it). */
export function applyBlack(on: boolean, root: HTMLElement = document.documentElement): void {
  if (on) root.dataset['black'] = ''; else delete root.dataset['black'];
  try { if (on) localStorage.setItem(BLACK_KEY, '1'); else localStorage.removeItem(BLACK_KEY); } catch { /* this page only */ }
}

export const BLACK_STORAGE_KEY = BLACK_KEY;
