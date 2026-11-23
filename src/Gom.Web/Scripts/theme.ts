export type Scheme = 'light' | 'dark';
const KEY = 'color-scheme';

/**
 * The two-state toggle: go to the opposite of what is showing now. If that is what the system already is, forget the
 * override (do not pin it); otherwise store it. A stored value that later matches the system is kept, never cleaned up.
 */
export function nextOverride({ stored, system }: { stored: Scheme | null; system: Scheme }): Scheme | null {
  const target: Scheme = (stored ?? system) === 'dark' ? 'light' : 'dark';
  return target === system ? null : target;
}

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

export const systemScheme = (): Scheme => (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
export const renderedScheme = (): Scheme => storedScheme() ?? systemScheme();
export const toggleScheme = (): void => applyScheme(nextOverride({ stored: storedScheme(), system: systemScheme() }));
