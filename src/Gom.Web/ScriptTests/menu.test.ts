import { describe, expect, it } from 'vitest';
import { placeMenu } from '../Scripts/menu.js';

/** A drawn menu of the given height (jsdom draws nothing, so its height is set by hand). */
const menuOf = (height: number) => {
  const pop = document.createElement('div');
  Object.defineProperty(pop, 'offsetHeight', { value: height });
  return pop;
};

describe('a row menu\'s place', () => {
  it('opens where it was asked for when it fits', () => {
    const pop = menuOf(120);
    placeMenu(pop, { x: 300.4, y: 150.6 });
    expect(pop.style.getPropertyValue('--x')).toBe('300px');
    expect(pop.style.getPropertyValue('--y')).toBe('151px');
  });

  it('moves up just enough to stay on the screen when opened low down', () => {
    const pop = menuOf(120);
    placeMenu(pop, { x: 10, y: innerHeight - 20 });
    expect(pop.style.getPropertyValue('--y')).toBe(`${innerHeight - 120 - 8}px`);
  });

  it('never goes above the top, even when taller than the screen', () => {
    const pop = menuOf(innerHeight + 100);
    placeMenu(pop, { x: 10, y: 40 });
    expect(pop.style.getPropertyValue('--y')).toBe('8px');
  });

  it('is left alone with nowhere asked for (a phone\'s sheet)', () => {
    const pop = menuOf(120);
    placeMenu(pop, undefined);
    expect(pop.style.getPropertyValue('--y')).toBe('');
  });
});
