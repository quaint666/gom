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
