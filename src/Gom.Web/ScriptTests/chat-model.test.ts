import { describe, expect, it } from 'vitest';
import type { ChatMessage } from '../Scripts/api.js';
import { buildThread, clock, dayLabel, initials, listTime, mergeMessages, type Shown } from '../Scripts/chat/model.js';

// Noon UTC is the same calendar day in every time zone from -11 to +11, so day grouping is the same wherever the tests run.
const at = (day: number, hh = 12, mm = 0) => new Date(Date.UTC(2026, 11, day, hh, mm)).toISOString();
const NOW = new Date(Date.UTC(2026, 11, 10, 12, 30));

const msg = (seq: number, from: string, when: string, body = `m${seq}`): ChatMessage => ({
  id: `id${seq}`, seq, conversationId: 'c1', clientId: `cl${seq}`, sender: { id: from, displayName: from.toUpperCase() }, body, createdAt: when,
});

describe('initials', () => {
  it.each([
    ['Minh Tâm', 'MT'], ['Mẹ', 'M'], ['lan_anh', 'L'], ['  Nguyễn  Văn  An ', 'NA'], ['', '?'], ['👨‍👩‍👧 Nhà', '👨‍👩‍👧N'], ['đỗ quyên', 'ĐQ'],
  ])('%s is %s', (name, expected) => { expect(initials(name)).toBe(expected); });
});

describe('time labels', () => {
  it('shows a clock as hours and minutes', () => { expect(clock(at(10, 9, 5))).toMatch(/^\d{2}:\d{2}$/); });

  it.each([
    [at(10), 'Hôm nay'], [at(9), 'Hôm qua'], [at(3), '03/12/2026'], [new Date(Date.UTC(2025, 11, 31, 12)).toISOString(), '31/12/2025'],
  ])('a day pill for %s says %s', (iso, label) => { expect(dayLabel(iso, NOW)).toBe(label); });

  it.each([[at(10, 12, 0), /^\d{2}:\d{2}$/], [at(9), 'Hôm qua'], [at(3), '3/12'], [new Date(Date.UTC(2025, 11, 31, 12)).toISOString(), '31/12/2025']])(
    'the list shows %s as %s', (iso, expected) => {
      const got = listTime(iso, NOW);
      if (expected instanceof RegExp) expect(got).toMatch(expected); else expect(got).toBe(expected);
    });
});

describe('same day', () => {
  it('is not fooled by the same day number in another month', () => {
    expect(dayLabel(new Date(Date.UTC(2026, 10, 10, 12)).toISOString(), NOW)).toBe('10/11/2026');
    expect(listTime(new Date(Date.UTC(2026, 10, 10, 12)).toISOString(), NOW)).toBe('10/11');
  });
});

describe('merging messages', () => {
  it('keeps messages in sequence order without repeats, and later copies win', () => {
    const a = msg(1, 'me', at(10)); const b = msg(2, 'lan', at(10));
    const merged = mergeMessages([b], [a, { ...b, body: 'edited' }, a]);
    expect(merged.map((m) => m.seq)).toEqual([1, 2]);
    expect(merged[1]!.body).toBe('edited');
  });

  it('does not change the lists it is given', () => {
    const left = [msg(2, 'me', at(10))]; const right = [msg(1, 'me', at(10))];
    mergeMessages(left, right);
    expect(left.map((m) => m.seq)).toEqual([2]);
    expect(right.map((m) => m.seq)).toEqual([1]);
  });
});

describe('building a thread', () => {
  const kinds = (items: ReturnType<typeof buildThread>) => items.map((i) => (i.kind === 'msg' ? `m${i.msg.seq}` : i.kind === 'day' ? `day:${i.label}` : `new:${i.count}`));

  it('puts a day pill before each new day', () => {
    const items = buildThread([msg(1, 'me', at(9)), msg(2, 'me', at(10, 9)), msg(3, 'me', at(10, 14))], 'me', null, 0, NOW);
    expect(kinds(items)).toEqual(['day:Hôm qua', 'm1', 'day:Hôm nay', 'm2', 'm3']);
  });

  it('marks the first and last of a run by one sender, and starts a new run for another sender', () => {
    const items = buildThread([msg(1, 'lan', at(10)), msg(2, 'lan', at(10)), msg(3, 'me', at(10)), msg(4, 'lan', at(10))], 'me', null, 0, NOW)
      .filter((i) => i.kind === 'msg');
    expect(items.map((i) => [i.first, i.last])).toEqual([[true, false], [false, true], [true, true], [true, true]]);
    expect(items.map((i) => i.mine)).toEqual([false, false, true, false]);
  });

  it('starts a new run after a gap of more than five minutes', () => {
    const items = buildThread([msg(1, 'lan', at(10, 12, 0)), msg(2, 'lan', at(10, 12, 4)), msg(3, 'lan', at(10, 12, 20))], 'me', null, 0, NOW)
      .filter((i) => i.kind === 'msg');
    expect(items.map((i) => [i.first, i.last])).toEqual([[true, false], [false, true], [true, true]]);
  });

  it('puts the "new messages" divider before the first unread message from someone else and counts them', () => {
    const items = buildThread([msg(1, 'lan', at(10)), msg(2, 'me', at(10)), msg(3, 'lan', at(10)), msg(4, 'lan', at(10))], 'me', 2, 2, NOW);
    expect(kinds(items)).toEqual(['day:Hôm nay', 'm1', 'm2', 'new:2', 'm3', 'm4']);
  });

  it('shows no divider when nothing was unread, or when the unread ones are not loaded', () => {
    const ms = [msg(1, 'lan', at(10)), msg(2, 'lan', at(10))];
    expect(kinds(buildThread(ms, 'me', null, 0, NOW))).toEqual(['day:Hôm nay', 'm1', 'm2']);
    expect(kinds(buildThread(ms, 'me', 9, 3, NOW))).toEqual(['day:Hôm nay', 'm1', 'm2']);
  });

  it('puts the divider before the first message that is not yours, even if one of yours comes first', () => {
    const items = buildThread([msg(1, 'lan', at(10)), msg(2, 'me', at(10)), msg(3, 'lan', at(10))], 'me', 1, 1, NOW);
    expect(kinds(items)).toEqual(['day:Hôm nay', 'm1', 'm2', 'new:1', 'm3']);
  });

  it('shows no divider when the count of new messages is zero', () => {
    expect(kinds(buildThread([msg(1, 'lan', at(10)), msg(2, 'lan', at(10))], 'me', 1, 0, NOW))).toEqual(['day:Hôm nay', 'm1', 'm2']);
  });

  it('starts a new run after midnight even within five minutes', () => {
    const before = new Date(2026, 11, 9, 23, 58).toISOString();
    const after = new Date(2026, 11, 10, 0, 1).toISOString();
    const items = buildThread([msg(1, 'lan', before), msg(2, 'lan', after)], 'me', null, 0, NOW).filter((i) => i.kind === 'msg');
    expect(items.map((i) => [i.first, i.last])).toEqual([[true, true], [true, true]]);
  });

  it('a divider also ends a run, so the next message shows its sender again', () => {
    const items = buildThread([msg(1, 'lan', at(10)), msg(2, 'lan', at(10))], 'me', 1, 1, NOW).filter((i) => i.kind === 'msg');
    expect(items.map((i) => [i.first, i.last])).toEqual([[true, true], [true, true]]);
  });

  it('puts messages that are still being sent at the end, in the order they were written, after the last day pill', () => {
    const pending: Shown[] = [
      { ...msg(0, 'me', at(10, 13)), id: 'p1', clientId: 'p1', state: 'sending' },
      { ...msg(0, 'me', at(10, 13, 1)), id: 'p2', clientId: 'p2', state: 'failed' },
    ];
    const items = buildThread([msg(1, 'lan', at(10))], 'me', null, 0, NOW, pending);
    expect(kinds(items)).toEqual(['day:Hôm nay', 'm1', 'm0', 'm0']);
    expect(items.filter((i) => i.kind === 'msg').map((i) => i.msg.clientId)).toEqual(['cl1', 'p1', 'p2']);
  });

  it('gives every item a different key, so the page can reuse what it already drew', () => {
    const items = buildThread([msg(1, 'lan', at(9)), msg(2, 'me', at(10))], 'me', 2, 1, NOW);
    expect(new Set(items.map((i) => i.key)).size).toBe(items.length);
  });
});
