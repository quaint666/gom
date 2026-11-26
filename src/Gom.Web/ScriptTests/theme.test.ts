import { beforeEach, describe, expect, it } from 'vitest';
import { PALETTES, applyBlack, applyPalette, applyScheme, chooseScheme, chosenScheme, isPalette, storedBlack, storedPalette, syncThemeColor } from '../Scripts/theme.js';

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

describe('the three-way choice', () => {
  beforeEach(() => {
    document.head.innerHTML = '<meta name="color-scheme" content="light dark">';
    localStorage.clear();
  });

  it('says "system" until light or dark is chosen', () => {
    expect(chosenScheme()).toBe('system');
    chooseScheme('dark');
    expect(chosenScheme()).toBe('dark');
    chooseScheme('light');
    expect(chosenScheme()).toBe('light');
  });

  it('goes back to following the system, forgetting the stored choice', () => {
    chooseScheme('dark');
    chooseScheme('system');
    expect(chosenScheme()).toBe('system');
    expect(localStorage.getItem('color-scheme')).toBeNull();
    expect(document.querySelector('meta[name="color-scheme"]')!.getAttribute('content')).toBe('light dark');
  });
});

describe('the person\'s colour', () => {
  beforeEach(() => {
    document.head.innerHTML = '<meta name="theme-color" content="#3F6B57">';
    delete document.documentElement.dataset['palette'];
    localStorage.clear();
  });

  it('is sage until one is chosen, marks the page with any other, and remembers it for the next page', () => {
    expect(storedPalette()).toBe('sage');
    applyPalette('lotus');
    expect(document.documentElement.dataset['palette']).toBe('lotus');
    expect(storedPalette()).toBe('lotus');
    applyPalette('sage');
    expect(document.documentElement.dataset['palette']).toBeUndefined();
    expect(storedPalette()).toBe('sage');
  });

  it('gives the browser\'s bar the colour the stylesheet resolves', () => {
    document.documentElement.style.setProperty('--btn', '#A34867');
    try {
      syncThemeColor();
      expect(document.querySelector('meta[name="theme-color"]')!.getAttribute('content')).toBe('#A34867');
    } finally { document.documentElement.style.removeProperty('--btn'); }
  });

  it('ignores a stored code it does not know', () => {
    localStorage.setItem('gom-palette', 'neon');
    expect(storedPalette()).toBe('sage');
    expect(isPalette('neon')).toBe(false);
    expect(PALETTES).toHaveLength(16);
  });
});

describe('pure black', () => {
  beforeEach(() => { localStorage.clear(); delete document.documentElement.dataset['black']; });

  it('is off until turned on, marks the page while on, and is remembered on this device only', () => {
    expect(storedBlack()).toBe(false);
    applyBlack(true);
    expect(document.documentElement.hasAttribute('data-black')).toBe(true);
    expect(storedBlack()).toBe(true);
    applyBlack(false);
    expect(document.documentElement.hasAttribute('data-black')).toBe(false);
    expect(localStorage.getItem('gom-black')).toBeNull();
  });
});
