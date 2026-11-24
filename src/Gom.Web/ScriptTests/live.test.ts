import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createLive, type LiveDeps, type LiveHandlers, type SocketLike } from '../Scripts/chat/live.js';

class FakeSocket implements SocketLike {
  static all: FakeSocket[] = [];
  onopen: (() => void) | null = null;
  onmessage: ((e: { data: string }) => void) | null = null;
  onclose: ((e: { code: number }) => void) | null = null;
  sent: string[] = [];
  closed = false;
  isOpen = false;
  constructor(public url: string) { FakeSocket.all.push(this); }
  send(data: string) { this.sent.push(data); }
  close() { this.closed = true; }
  get open() { return this.isOpen; }
  // test controls
  accept() { this.isOpen = true; this.onopen?.(); }
  say(frame: unknown) { this.onmessage?.({ data: typeof frame === 'string' ? frame : JSON.stringify(frame) }); }
  drop(code = 1006) { this.isOpen = false; this.onclose?.({ code }); }
}

let timers: { id: number; ms: number; fn: () => void }[];
let nextTimer: number;
const deps = (random = 1): LiveDeps => ({
  socket: (url) => new FakeSocket(url),
  timer: {
    set: (fn, ms) => { const id = ++nextTimer; timers.push({ id, ms, fn }); return id; },
    clear: (id) => { timers = timers.filter((t) => t.id !== id); },
  },
  random: () => random,
});

function handlers(): LiveHandlers & { [K in keyof LiveHandlers]: ReturnType<typeof vi.fn> } {
  return { state: vi.fn(), event: vi.fn(), events: vi.fn(), conversations: vi.fn(), read: vi.fn(), hidden: vi.fn(), ended: vi.fn(), replaced: vi.fn() } as never;
}
const last = () => FakeSocket.all[FakeSocket.all.length - 1]!;
const fireTimer = () => { const t = timers.shift()!; t.fn(); };

beforeEach(() => { FakeSocket.all = []; timers = []; nextTimer = 0; });

describe('live socket', () => {
  it('connects, and is "connected" only once the server says ready', () => {
    const h = handlers();
    createLive('ws://x/ws', h, deps()).start();
    expect(last().url).toBe('ws://x/ws');
    last().accept();
    expect(h.state).not.toHaveBeenCalledWith(true);
    last().say({ t: 'ready' });
    expect(h.state).toHaveBeenLastCalledWith(true);
  });

  it('passes live events and sync pages on, and ignores rubbish', () => {
    const h = handlers();
    createLive('ws://x/ws', h, deps()).start();
    last().accept(); last().say({ t: 'ready' });
    const event = { seq: 1, kind: 'created', message: { id: 'm' } };

    last().say({ t: 'event', c: 'c1', event });
    last().say({ t: 'events', c: 'c1', events: [event], hasMore: true });
    last().say('not json'); last().say({ t: 'mystery' }); last().say({ t: 'error', error: 'not_found' }); last().say('[]');

    expect(h.event).toHaveBeenCalledOnce();
    expect(h.event).toHaveBeenCalledWith('c1', event);
    expect(h.events).toHaveBeenCalledOnce();
    expect(h.events).toHaveBeenCalledWith('c1', [event], true);
  });

  it('tells the page how far someone has read', () => {
    const h = handlers();
    createLive('ws://x/ws', h, deps()).start();
    last().accept(); last().say({ t: 'ready' });
    last().say({ t: 'read', c: 'c1', u: 'u-lan', seq: 7 });
    last().say({ t: 'read', c: 'c1', u: 5, seq: 3 });            // not well formed: ignored
    last().say({ t: 'read', c: 'c1', u: 'u-lan', seq: 'x' });
    expect(h.read).toHaveBeenCalledOnce();
    expect(h.read).toHaveBeenCalledWith('c1', 'u-lan', 7);
  });

  it('tells the page which message you deleted for yourself on another device', () => {
    const h = handlers();
    createLive('ws://x/ws', h, deps()).start();
    last().accept(); last().say({ t: 'ready' });
    last().say({ t: 'hidden', c: 'c1', m: 'msg-9' });
    last().say({ t: 'hidden', c: 'c1', m: 7 });          // not well formed: ignored
    last().say({ t: 'hidden', m: 'msg-9' });
    expect(h.hidden).toHaveBeenCalledOnce();
    expect(h.hidden).toHaveBeenCalledWith('c1', 'msg-9');
  });

  it('tells the page when its list of conversations changed', () => {
    const h = handlers();
    createLive('ws://x/ws', h, deps()).start();
    last().accept(); last().say({ t: 'ready' });
    last().say({ t: 'conversations' });
    expect(h.conversations).toHaveBeenCalledOnce();
  });

  it('asks for what it missed with a sync frame, only while open', () => {
    const live = createLive('ws://x/ws', handlers(), deps());
    live.start();
    live.sync('c1', 5);
    expect(last().sent).toEqual([]);
    last().accept(); last().say({ t: 'ready' });
    live.sync('c1', 5);
    expect(last().sent.map((s) => JSON.parse(s))).toEqual([{ t: 'sync', c: 'c1', after: 5 }]);
  });

  it('reconnects after a drop, waiting longer each time, and starts again once ready', () => {
    const h = handlers();
    createLive('ws://x/ws', h, deps(1)).start();
    last().accept(); last().say({ t: 'ready' });
    last().drop();
    expect(h.state).toHaveBeenLastCalledWith(false);
    expect(timers.map((t) => t.ms)).toEqual([1000]);

    fireTimer(); last().drop();               // never got ready
    expect(timers.map((t) => t.ms)).toEqual([2000]);
    fireTimer(); last().drop();
    expect(timers.map((t) => t.ms)).toEqual([4000]);

    fireTimer(); last().accept(); last().say({ t: 'ready' }); last().drop();
    expect(timers.map((t) => t.ms)).toEqual([1000]);   // a good connection resets the wait
    expect(FakeSocket.all).toHaveLength(4);
  });

  it('never waits more than thirty seconds, and spreads the wait so devices do not all return together', () => {
    createLive('ws://x/ws', handlers(), deps(1)).start();
    for (let i = 0; i < 12; i++) { last().drop(); fireTimer(); }
    last().drop();
    expect(timers[0]!.ms).toBe(30_000);

    FakeSocket.all = []; timers = [];
    createLive('ws://x/ws', handlers(), deps(0)).start();
    last().drop();
    expect(timers[0]!.ms).toBe(500);   // half of the first wait at the lowest roll
  });

  it('does not reconnect when the session has ended, and says so', () => {
    const h = handlers();
    createLive('ws://x/ws', h, deps()).start();
    last().accept(); last().say({ t: 'ready' });
    last().drop(4401);
    expect(h.ended).toHaveBeenCalledOnce();
    expect(timers).toEqual([]);
  });

  it('does not reconnect when another window took the place, and says so', () => {
    const h = handlers();
    createLive('ws://x/ws', h, deps()).start();
    last().accept(); last().say({ t: 'ready' });
    last().drop(1008);
    expect(h.replaced).toHaveBeenCalledOnce();
    expect(timers).toEqual([]);
  });

  it('ignores a late second close from a socket it already gave up on', () => {
    const h = handlers();
    createLive('ws://x/ws', h, deps()).start();
    const old = last();
    old.drop();
    fireTimer();
    expect(FakeSocket.all).toHaveLength(2);
    h.state.mockClear();

    old.drop();   // the old one reports again, after the new one exists

    expect(h.state).not.toHaveBeenCalled();
    expect(timers).toEqual([]);
  });

  it('reconnects after a "too slow" close (1013)', () => {
    createLive('ws://x/ws', handlers(), deps()).start();
    last().accept(); last().say({ t: 'ready' });
    last().drop(1013);
    expect(timers).toHaveLength(1);
  });

  it('stop closes the socket and cancels any pending reconnect', () => {
    const live = createLive('ws://x/ws', handlers(), deps());
    live.start();
    const first = last();
    first.accept(); first.say({ t: 'ready' }); first.drop();
    expect(timers).toHaveLength(1);
    live.stop();
    expect(timers).toEqual([]);
    const again = createLive('ws://x/ws', handlers(), deps());
    again.start(); const second = last(); again.stop();
    expect(second.closed).toBe(true);
    second.drop();
    expect(timers).toEqual([]);
  });

  it('retryNow connects at once instead of waiting, but not when already connected or connecting', () => {
    const live = createLive('ws://x/ws', handlers(), deps());
    live.start(); last().accept(); last().say({ t: 'ready' });
    live.retryNow();
    expect(FakeSocket.all).toHaveLength(1);
    last().drop();
    expect(timers).toHaveLength(1);
    live.retryNow();
    expect(timers).toEqual([]);
    expect(FakeSocket.all).toHaveLength(2);
    live.retryNow();
    expect(FakeSocket.all).toHaveLength(2);   // still connecting
  });

  it('reports "not connected" when the first attempt fails, so a banner can show', () => {
    const h = handlers();
    createLive('ws://x/ws', h, deps()).start();
    last().drop();
    expect(h.state).toHaveBeenCalledWith(false);
  });
});
