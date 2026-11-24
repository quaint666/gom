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
  /** The session ended: stop trying and go to sign-in. */
  ended(): void;
  /** Another window took this one's place: stop trying (or two windows would take turns forever). */
  replaced(): void;
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
  timer: { set(fn: () => void, ms: number): number; clear(id: number): void };
  random(): number;
}

const realDeps: LiveDeps = {
  socket: (url) => new WebSocket(url) as unknown as SocketLike,
  timer: { set: (fn, ms) => window.setTimeout(fn, ms), clear: (id) => window.clearTimeout(id) },
  random: () => Math.random(),
};

const MAX_WAIT_MS = 30_000;

export function createLive(url: string, handlers: LiveHandlers, deps: LiveDeps = realDeps): LiveControl {
  let socket: SocketLike | null = null;
  let ready = false;
  let attempt = 0;
  let timer: number | null = null;
  let stopped = true;

  function connect(): void {
    const s = deps.socket(url);
    socket = s;
    ready = false;
    s.onmessage = (e) => {
      let frame: { t?: unknown; c?: unknown; event?: unknown; events?: unknown; hasMore?: unknown };
      try { frame = JSON.parse(e.data) as typeof frame; } catch { return; }
      if (typeof frame !== 'object' || frame === null) return;
      if (frame.t === 'ready') { ready = true; attempt = 0; handlers.state(true); }
      else if (frame.t === 'event' && typeof frame.c === 'string') handlers.event(frame.c, frame.event as ChatEvent);
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
      s?.close();
    },
    sync(conversation, after) { if (ready && socket) socket.send(JSON.stringify({ t: 'sync', c: conversation, after })); },
    retryNow() {
      if (stopped || socket !== null) return;
      if (timer !== null) { deps.timer.clear(timer); timer = null; }
      connect();
    },
  };
}
