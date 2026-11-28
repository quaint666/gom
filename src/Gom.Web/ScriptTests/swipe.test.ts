import { describe, expect, it } from 'vitest';
import { SWIPE_READY, swipeReady, swiped } from '../Scripts/swipe.js';

describe('swiped: how far a message moves for a finger', () => {
  it('moves someone else\'s message right, one for one up to the ready distance', () => {
    expect(swiped(0, false)).toBe(0);
    expect(swiped(25, false)).toBe(25);
    expect(swiped(SWIPE_READY, false)).toBe(SWIPE_READY);
  });

  it('moves your own message left, as the same distance', () => {
    expect(swiped(-25, true)).toBe(25);
    expect(swiped(-SWIPE_READY, true)).toBe(SWIPE_READY);
  });

  it('moves nothing for a drag the other way', () => {
    expect(swiped(-40, false)).toBe(0);
    expect(swiped(40, true)).toBe(0);
  });

  it('holds back past the ready distance, and stops at a limit', () => {
    const past = swiped(SWIPE_READY + 100, false);
    expect(past).toBeGreaterThan(SWIPE_READY);
    expect(past).toBeLessThan(SWIPE_READY + 100);
    expect(swiped(5000, false)).toBeLessThanOrEqual(100);
    expect(swiped(-5000, true)).toBeLessThanOrEqual(100);
  });

  it('is ready from the ready distance, not before', () => {
    expect(swipeReady(SWIPE_READY - 1)).toBe(false);
    expect(swipeReady(SWIPE_READY)).toBe(true);
    expect(swipeReady(swiped(SWIPE_READY + 1, false))).toBe(true);
  });
});
