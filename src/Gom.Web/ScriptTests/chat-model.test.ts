import { describe, expect, it } from 'vitest';
import type { CallLine, ChatMessage } from '../Scripts/api.js';
import { AVATAR_TONES, avatarTone, buildThread, callLength, callPreview, callTitle, clock, dayLabel, initials, listTime, mergeMessages, missedByMe, noticeText, type Shown } from '../Scripts/chat/model.js';

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
  const kinds = (items: ReturnType<typeof buildThread>) => items.map((i) => (i.kind === 'msg' ? `m${i.msg.seq}` : i.kind === 'day' ? `day:${i.label}` : i.kind === 'notice' ? `notice:${i.msg.seq}` : `new:${i.count}`));

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

describe('avatarTone', () => {
  it('is always one of the colours, and the same for the same name whatever the case or spaces', () => {
    for (const name of ['Mẹ', 'Linh Nguyễn', 'Đào Thị Lan', 'a', '😀', '']) {
      const tone = avatarTone(name);
      expect(tone).toBeGreaterThanOrEqual(1);
      expect(tone).toBeLessThanOrEqual(AVATAR_TONES);
      expect(avatarTone(name)).toBe(tone);
    }
    expect(avatarTone('  Linh NGUYỄN ')).toBe(avatarTone('linh nguyễn'));
  });

  it('uses more than one colour across a family of names (it is there for variety)', () => {
    const names = ['Bảo', 'Mai Trần', 'Hùng', 'Lan Anh', 'Nam', 'Thu Hà', 'Tommy', 'Linh Nguyễn'];
    expect(new Set(names.map(avatarTone)).size).toBeGreaterThanOrEqual(4);
  });
});

describe('notices in a thread', () => {
  const sender = (id: string, displayName: string) => ({ id, displayName });
  const notice = (n: number, who: string, type: 'added' | 'removed' | 'left' | 'renamed' | 'restyled' | 'pictured', target?: { id: string; displayName: string }, title?: string): ChatMessage =>
    ({ ...msg(n, who, at(10, 12, n)), body: '', sender: sender(who === 'me' ? 'me' : `u-${who}`, who), notice: { type, target: target ?? null, title: title ?? null } });

  it('puts a notice between messages, breaks the run, and is never the first of a new-message divider', () => {
    const items = buildThread([msg(1, 'lan', at(10, 12, 1)), notice(2, 'lan', 'added', sender('u-bao', 'Bảo')), msg(3, 'lan', at(10, 12, 3))], 'me', 0, 2, NOW);   // a minute or two apart: one run, but for the notice
    expect(items.map((i) => i.kind)).toEqual(['day', 'new', 'msg', 'notice', 'msg']);
    const msgs = items.filter((i) => i.kind === 'msg');
    expect(msgs.map((i) => i.kind === 'msg' && [i.first, i.last])).toEqual([[true, true], [true, true]]);   // the notice split what was one run
  });

  it('does not put the divider before a notice even when it is the first thing after what was read', () => {
    const items = buildThread([notice(1, 'lan', 'left'), msg(2, 'lan', at(10, 2))], 'me', 0, 1, NOW);
    const order = items.map((i) => i.kind);
    expect(order.indexOf('notice')).toBeLessThan(order.indexOf('new'));
  });

  it('says what happened, from the reader\'s side', () => {
    expect(noticeText(notice(1, 'lan', 'added', sender('u-bao', 'Bảo')), 'me')).toBe('lan đã thêm Bảo vào nhóm');
    expect(noticeText(notice(1, 'lan', 'added', sender('me', 'Mẹ')), 'me')).toBe('lan đã thêm bạn vào nhóm');
    expect(noticeText(notice(1, 'me', 'added', sender('u-bao', 'Bảo')), 'me')).toBe('Bạn đã thêm Bảo vào nhóm');
    expect(noticeText(notice(1, 'lan', 'pictured'), 'me')).toBe('lan đã đổi ảnh nhóm');
    expect(noticeText(notice(1, 'lan', 'removed', sender('u-bao', 'Bảo')), 'me')).toBe('lan đã xóa Bảo khỏi nhóm');
    expect(noticeText(notice(1, 'lan', 'left'), 'me')).toBe('lan đã rời nhóm');
    expect(noticeText(notice(1, 'me', 'renamed', undefined, 'Nhà mình'), 'me')).toBe('Bạn đã đổi tên nhóm thành "Nhà mình"');
    expect(noticeText(notice(1, 'lan', 'restyled'), 'me')).toBe('lan đã đổi màu và hình nền của cuộc trò chuyện');
    expect(noticeText(msg(1, 'lan', at(10, 1)), 'me')).toBe('');
  });
});

describe('call lines', () => {
  const line = (outcome: CallLine['outcome'], callerId = 'u-a', seconds = 0): CallLine => ({ callerId, outcome, startedAt: '2026-12-10T08:12:00Z', seconds });

  it('word each ending for whoever reads it', () => {
    const table = (['completed', 'missed', 'declined', 'busy', 'failed'] as const).map((o) => [o, callTitle(line(o), 'u-a'), callTitle(line(o), 'u-b')]);
    expect(table).toEqual([
      ['completed', 'Cuộc gọi thoại', 'Cuộc gọi thoại'],
      ['missed', 'Không trả lời', 'Cuộc gọi nhỡ'],
      ['declined', 'Cuộc gọi bị từ chối', 'Bạn đã từ chối'],
      ['busy', 'Máy bận', 'Cuộc gọi nhỡ'],
      ['failed', 'Không kết nối được', 'Không kết nối được'],
    ]);
  });

  it('count a call as missed only for the person called, when it rang out or found them busy', () => {
    expect(missedByMe(line('missed'), 'u-b')).toBe(true);
    expect(missedByMe(line('busy'), 'u-b')).toBe(true);
    expect(missedByMe(line('missed'), 'u-a')).toBe(false);
    expect(missedByMe(line('declined'), 'u-b')).toBe(false);
  });

  it('say how long, past an hour too', () => {
    expect(callLength(0)).toBe('0:00');
    expect(callLength(185)).toBe('3:05');
    expect(callLength(3729)).toBe('1:02:09');
    expect(callPreview(line('completed', 'u-a', 185), 'u-b')).toBe('Cuộc gọi thoại · 3:05');
    expect(callPreview(line('missed'), 'u-b')).toBe('Cuộc gọi nhỡ');
  });
});
