import type { ChatEvent } from '../api.js';

/** The part of a WebSocket we use, so tests can stand in for it. */
export interface SocketLike {
  onopen: (() => void) | null;
  onmessage: ((e: { data: string }) => void) | null;
  onclose: ((e: { code: number }) => void) | null;
  send(data: string): void;
  close(): void;
}

export interface LiveHandlers {
  /** True once the server says it is ready; false when the connection is lost or could not be made. */
  state(connected: boolean): void;
  event(conversation: string, event: ChatEvent): void;
  /** A page of what was missed, in order. */
  events(conversation: string, events: ChatEvent[], hasMore: boolean): void;
  /** Your list of conversations changed (a group was made, you were added, someone left): load it again. */
  conversations(): void;
  /** Someone has now read up to this number (only sent if you and they both share your reading). */
  read(conversation: string, userId: string, seq: number): void;
  /** The pinned messages of this conversation changed: ask for them again. */
  pins(conversation: string): void;
  /** You deleted this message for yourself, on another device: drop it here too. */
  hidden(conversation: string, message: string): void;
  /** You deleted this whole conversation for yourself, on another device, up to this number: drop what this page holds of it. */
  cleared(conversation: string, seq: number): void;
  /** The server turned away a request to catch up (too many too fast): ask again about this conversation in a moment. */
  slow(conversation: string): void;
  /** The session ended: stop trying and go to sign-in. */
  ended(): void;
  /** Another window took this one's place: stop trying (or two windows would take turns forever). */
  replaced(): void;
  /** The version of the pages the server hands out, told on every connect (after a deploy, the first reconnect tells the new one). */
  version?(version: string): void;
  /** The server no longer works with this page's code (closed with 4426): stop trying, the page must reload. */
  tooOld?(): void;
}

export interface LiveControl {
  start(): void;
  stop(): void;
  /** Ask for the events after this sequence number. Only while connected. */
  sync(conversation: string, after: number): void;
  /** Try now instead of waiting: the network came back, or the page was shown again. */
  retryNow(): void;
}

export interface LiveDeps {
  socket(url: string): SocketLike;
  /** Milliseconds, any steady clock. */
  now(): number;
  /** Calls fn every `ms` until the returned function is called. */
  interval(fn: () => void, ms: number): () => void;
  timer: { set(fn: () => void, ms: number): number; clear(id: number): void };
  random(): number;
}

const realDeps: LiveDeps = {
  socket: (url) => new WebSocket(url) as unknown as SocketLike,
  now: () => Date.now(),
  interval: (fn, ms) => { const id = window.setInterval(fn, ms); return () => window.clearInterval(id); },
  timer: { set: (fn, ms) => window.setTimeout(fn, ms), clear: (id) => window.clearTimeout(id) },
  random: () => Math.random(),
};

const MAX_WAIT_MS = 30_000;
/** The server sends "still here" every 25 s. This long with nothing at all means the line is dead even if the browser does not know it. */
const SILENT_MS = 60_000;
/** How often the page checks for that. */
const WATCH_EVERY_MS = 15_000;
/** When the tab is shown again or the network returns, silence this long already proves the connection is gone (two beats missed). */
const STALE_ON_RETURN_MS = 35_000;

export function createLive(url: string, handlers: LiveHandlers, deps: LiveDeps = realDeps): LiveControl {
  let socket: SocketLike | null = null;
  let ready = false;
  let attempt = 0;
  let timer: number | null = null;
  let stopped = true;
  let lastFrameAt = 0;
  let stopWatching: (() => void) | null = null;

  /** Lets go of a connection the browser still calls open, and comes back at once: nothing arrives on it. */
  function abandon(): void {
    const s = socket;
    if (s === null) return;
    socket = null;
    ready = false;
    s.onclose = null;
    s.onmessage = null;
    try { s.close(); } catch { /* it is gone either way */ }
    handlers.state(false);
    if (!stopped) connect();
  }

  function connect(): void {
    const s = deps.socket(url);
    socket = s;
    ready = false;
    lastFrameAt = deps.now();
    stopWatching ??= deps.interval(() => { if (socket !== null && deps.now() - lastFrameAt > SILENT_MS) abandon(); }, WATCH_EVERY_MS);
    s.onmessage = (e) => {
      if (socket !== s) return;
      lastFrameAt = deps.now();
      let frame: { t?: unknown; v?: unknown; c?: unknown; u?: unknown; seq?: unknown; event?: unknown; events?: unknown; hasMore?: unknown; m?: unknown; error?: unknown };
      try { frame = JSON.parse(e.data) as typeof frame; } catch { return; }
      if (typeof frame !== 'object' || frame === null) return;
      if (frame.t === 'ready') { ready = true; attempt = 0; handlers.state(true); if (typeof frame.v === 'string') handlers.version?.(frame.v); }
      else if (frame.t === 'event' && typeof frame.c === 'string') handlers.event(frame.c, frame.event as ChatEvent);
      else if (frame.t === 'conversations') handlers.conversations();
      else if (frame.t === 'read' && typeof frame.c === 'string' && typeof frame.u === 'string' && typeof frame.seq === 'number') handlers.read(frame.c, frame.u, frame.seq);
      else if (frame.t === 'pins' && typeof frame.c === 'string') handlers.pins(frame.c);
      else if (frame.t === 'hidden' && typeof frame.c === 'string' && typeof frame.m === 'string') handlers.hidden(frame.c, frame.m);
      else if (frame.t === 'cleared' && typeof frame.c === 'string' && typeof frame.seq === 'number') handlers.cleared(frame.c, frame.seq);
      else if (frame.t === 'error' && frame.error === 'slow_down' && typeof frame.c === 'string') handlers.slow(frame.c);
      else if (frame.t === 'events' && typeof frame.c === 'string' && Array.isArray(frame.events)) handlers.events(frame.c, frame.events as ChatEvent[], frame.hasMore === true);
    };
    s.onclose = (e) => {
      if (socket !== s) return;
      socket = null;
      ready = false;
      handlers.state(false);
      if (stopped) return;
      if (e.code === 4401) { stopped = true; handlers.ended(); return; }
      if (e.code === 1008) { stopped = true; handlers.replaced(); return; }
      if (e.code === 4426) { stopped = true; handlers.tooOld?.(); return; }
      // Wait 1 s, 2 s, 4 s ... up to 30 s, with a random share taken off so devices do not all come back together.
      const wait = Math.min(MAX_WAIT_MS, 1000 * 2 ** attempt) * (0.5 + deps.random() * 0.5);
      attempt++;
      timer = deps.timer.set(() => { timer = null; connect(); }, wait);
    };
  }

  return {
    start() { stopped = false; if (socket === null && timer === null) connect(); },
    stop() {
      stopped = true;
      if (timer !== null) { deps.timer.clear(timer); timer = null; }
      const s = socket;
      socket = null;
      stopWatching?.();
      stopWatching = null;
      s?.close();
    },
    sync(conversation, after) { if (ready && socket) socket.send(JSON.stringify({ t: 'sync', c: conversation, after })); },
    retryNow() {
      if (stopped) return;
      // The browser still calls it open, but nothing has come for a while: it is not.
      if (socket !== null) { if (deps.now() - lastFrameAt > STALE_ON_RETURN_MS) abandon(); return; }
      if (timer !== null) { deps.timer.clear(timer); timer = null; }
      connect();
    },
  };
}
