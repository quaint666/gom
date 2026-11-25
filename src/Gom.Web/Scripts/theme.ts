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
export const PALETTES = ['sage', 'sea', 'lotus', 'turmeric', 'lavender', 'graphite', 'jade', 'indigo', 'plum', 'lacquer', 'coffee', 'moss'] as const;
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
