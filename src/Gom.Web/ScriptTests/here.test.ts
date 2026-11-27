import { beforeEach, describe, expect, it } from 'vitest';
import { chooseTakeCalls, chosenTakeCalls, rememberAppCalls, takesCallsHere } from '../Scripts/calls/here.js';

beforeEach(() => { localStorage.clear(); });

describe('whether this browser rings for calls', () => {
  it('rings on a computer, and on a phone whose person has no app taking calls', () => {
    expect(takesCallsHere(false)).toBe(true);
    expect(takesCallsHere(true)).toBe(true);
    rememberAppCalls(false);
    expect(takesCallsHere(true)).toBe(true);
  });

  it('does not ring on a phone whose person has the app taking calls (the app rings), but a computer still does', () => {
    rememberAppCalls(true);
    expect(takesCallsHere(true)).toBe(false);
    expect(takesCallsHere(false)).toBe(true);
  });

  it('follows what was chosen for this browser, either way, over the default', () => {
    rememberAppCalls(true);
    chooseTakeCalls(true);
    expect(chosenTakeCalls()).toBe(true);
    expect(takesCallsHere(true)).toBe(true);
    chooseTakeCalls(false);
    expect(takesCallsHere(false)).toBe(false);
  });
});
