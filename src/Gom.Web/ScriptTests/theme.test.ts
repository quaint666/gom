import { beforeEach, describe, expect, it } from 'vitest';
import { nextOverride, applyScheme } from '../Scripts/theme.js';

describe('nextOverride (two-state toggle)', () => {
  it('stores the opposite when it differs from the system', () => {
    expect(nextOverride({ stored: null, system: 'light' })).toBe('dark');
    expect(nextOverride({ stored: null, system: 'dark' })).toBe('light');
  });

  it('removes the override when the user undoes it', () => {
    expect(nextOverride({ stored: 'dark', system: 'light' })).toBeNull();
    expect(nextOverride({ stored: 'light', system: 'dark' })).toBeNull();
  });

  it('keeps pinning against the system when the stored value now matches it, and toggles the other way', () => {
    // The OS later changed to dark and the site was pinned dark: rendered is dark, so the toggle goes to light,
    // which differs from the system (dark), so it is stored.
    expect(nextOverride({ stored: 'dark', system: 'dark' })).toBe('light');
  });
});

describe('applyScheme', () => {
  beforeEach(() => {
    document.head.innerHTML = '<meta name="color-scheme" content="light dark">';
    localStorage.clear();
  });

  it('writes the meta and the storage for an override', () => {
    applyScheme('dark');
    expect(document.querySelector('meta[name="color-scheme"]')!.getAttribute('content')).toBe('dark');
    expect(localStorage.getItem('color-scheme')).toBe('dark');
  });

  it('goes back to "light dark" and clears the storage for null', () => {
    applyScheme('dark');
    applyScheme(null);
    expect(document.querySelector('meta[name="color-scheme"]')!.getAttribute('content')).toBe('light dark');
    expect(localStorage.getItem('color-scheme')).toBeNull();
  });

  it('still applies the scheme when storage is blocked', () => {
    const real = Storage.prototype.setItem;
    Storage.prototype.setItem = () => { throw new DOMException('blocked'); };
    try {
      applyScheme('dark');
      expect(document.querySelector('meta[name="color-scheme"]')!.getAttribute('content')).toBe('dark');
    } finally { Storage.prototype.setItem = real; }
  });
});
