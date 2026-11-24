import { beforeEach, describe, expect, it } from 'vitest';
import { applyScheme, chooseScheme, chosenScheme } from '../Scripts/theme.js';

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
