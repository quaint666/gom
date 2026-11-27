import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { CallInfo, CallServers } from '../Scripts/api.js';
import { CONNECT_MS, createCalls, ENDED_MS, GATHER_MS, GIVE_UP_MS, RECONNECT_AFTER_MS, RENEW_BEFORE_S, type CallApi, type CallDeps, type PeerLike } from '../Scripts/calls/call.js';
import { fail, flush, ok } from './helpers.js';

const CHAT = 'c-1';
const ID = 'call-1';
const servers: CallServers = { iceServers: [{ urls: 'stun:stun.example:3478' }], relay: false, seconds: 10_800 };
const info = (over: Partial<CallInfo> = {}): CallInfo => ({
  id: ID, conversationId: CHAT, state: 'ringing', outcome: null, callerId: 'u-me', calleeId: 'u-mom', caller: { id: 'u-me', displayName: 'Con' },
  offer: null, answer: null, ringSeconds: 45, aliveSeconds: 30, ...over,
});

/** A WebRTC connection that does what a test tells it: gathering finishes when `gatherNow()` is called, the state moves on `state()`. */
class FakePeer implements PeerLike {
  local: RTCSessionDescriptionInit | null = null;
  remote: RTCSessionDescriptionInit[] = [];
  tracks: MediaStreamTrack[] = [];
  closed = false;
  iceGatheringState = 'new';
  connectionState = 'new';
  signalingState = 'stable';
  configs: RTCConfiguration[] = [];
  offers: (RTCOfferOptions | undefined)[] = [];
  restarts = 0;
  onicegatheringstatechange: (() => void) | null = null;
  onconnectionstatechange: (() => void) | null = null;
  ontrack: ((e: { streams: readonly MediaStream[] }) => void) | null = null;
  constructor(readonly config: RTCConfiguration) {}
  get localDescription() { return this.local === null ? null : { sdp: `${this.local.sdp}+addresses` }; }
  addTrack(track: MediaStreamTrack) { this.tracks.push(track); }
  async createOffer(options?: RTCOfferOptions) { this.offers.push(options); return { type: 'offer' as const, sdp: options?.iceRestart ? 'v=0 offer again' : 'v=0 offer' }; }
  async createAnswer() { return { type: 'answer' as const, sdp: 'v=0 answer' }; }
  async setLocalDescription(d: RTCSessionDescriptionInit) { this.local = d; this.iceGatheringState = 'gathering'; this.signalingState = d.type === 'offer' ? 'have-local-offer' : 'stable'; }
  async setRemoteDescription(d: RTCSessionDescriptionInit) { this.remote.push(d); this.signalingState = d.type === 'offer' ? 'have-remote-offer' : 'stable'; }
  setConfiguration(c: RTCConfiguration) { this.configs.push(c); }
  restartIce() { this.restarts++; }
  close() { this.closed = true; }
  gatherNow() { this.iceGatheringState = 'complete'; this.onicegatheringstatechange?.(); }
  state(s: string) { this.connectionState = s; this.onconnectionstatechange?.(); }
}

function fakeTrack() { return { enabled: true, stopped: false, stop() { this.stopped = true; } }; }

function setup(over: { api?: Partial<{ [K in keyof CallApi]: ReturnType<typeof vi.fn> }>; mic?: () => Promise<MediaStream>; phone?: boolean; takesCalls?: boolean } = {}) {
  const host = document.createElement('div');
  document.body.replaceChildren(host);
  const peers: FakePeer[] = [];
  const track = fakeTrack();
  const stream = { getTracks: () => [track], getAudioTracks: () => [track] } as unknown as MediaStream;
  let clock = 0;
  const timers = new Map<number, { at: number; fn: () => void }>();
  let nextTimer = 1;
  const api = {
    callServers: vi.fn(async () => ok(servers)),
    startCall: vi.fn(async () => ok(info())),
    getCall: vi.fn(async () => ok(info({ offer: 'v=0 their offer' }))),
    acceptCall: vi.fn(async () => ok(info({ state: 'active' }))),
    declineCall: vi.fn(async () => ok(undefined)),
    hangUp: vi.fn(async () => ok(undefined)),
    callSdp: vi.fn(async () => ok({ rev: 1 })),
    callAlive: vi.fn(async () => ok(undefined)),
    ...over.api,
  };
  const ring = { incoming: vi.fn<() => void>(), outgoing: vi.fn<() => void>(), stop: vi.fn<() => void>() };
  const deps: CallDeps & { ring: typeof ring } = {
    api: api as unknown as CallApi, host,
    peer: (config) => { const p = new FakePeer(config); peers.push(p); return p; },
    mic: over.mic ?? (async () => stream),
    play: vi.fn(),
    ring,
    title: vi.fn(),
    guardLeaving: vi.fn(),
    phone: () => over.phone ?? false,
    takesCalls: () => over.takesCalls ?? true,
    now: () => clock,
    timer: { set: (fn, ms) => { const id = nextTimer++; timers.set(id, { at: clock + ms, fn }); return id; }, clear: (id) => { timers.delete(id); } },
    newId: () => 'client-1',
  };
  /** Moves the clock on, running each timer that comes due, in order. */
  const pass = async (ms: number) => {
    const until = clock + ms;
    for (;;) {
      const due = [...timers.entries()].filter(([, t]) => t.at <= until).sort((a, b) => a[1].at - b[1].at)[0];
      if (due === undefined) break;
      timers.delete(due[0]);
      clock = due[1].at;
      due[1].fn();
      for (let i = 0; i < 6; i++) await Promise.resolve();   // what the timer started settles (faster than a full flush, over hours)
    }
    clock = until;
    await flush();
  };
  const calls = createCalls(deps);
  const text = () => host.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const press = async (which: string) => { host.querySelector<HTMLButtonElement>(`[data-call="${which}"]`)!.click(); await flush(); };
  return { calls, api, deps, host, peers, track, pass, text, press };
}

/** Calls and waits until the offer has gone and it rings on the other side. */
async function ringingOut(t: ReturnType<typeof setup>) {
  const started = t.calls.start(CHAT, { name: 'Mẹ' });
  await flush(); await flush();
  t.peers[0]!.gatherNow();
  await started;
}

beforeEach(() => { document.title = 'Gom'; });

describe('calls: calling a friend', () => {
  it('takes the microphone, asks for the servers, sends the offer with every address, and rings', async () => {
    const t = setup();
    await ringingOut(t);
    expect(t.api.callServers).toHaveBeenCalledWith({ conversationId: CHAT });
    expect(t.peers[0]!.config.iceServers).toEqual(servers.iceServers);
    expect(t.peers[0]!.tracks).toHaveLength(1);
    expect(t.api.startCall).toHaveBeenCalledWith(CHAT, 'client-1', 'v=0 offer+addresses');
    expect(t.text()).toContain('Mẹ');
    expect(t.text()).toContain('Đang đổ chuông…');
    expect(t.deps.ring.outgoing).toHaveBeenCalled();
    expect(t.deps.guardLeaving).toHaveBeenLastCalledWith(true);
    expect(t.calls.busy()).toBe(true);
  });

  it('sends the offer with what it has after 3 s when the addresses are slow', async () => {
    const t = setup();
    void t.calls.start(CHAT, { name: 'Mẹ' });
    await flush(); await flush();
    await t.pass(GATHER_MS - 1);
    expect(t.api.startCall).not.toHaveBeenCalled();
    await t.pass(1);
    expect(t.api.startCall).toHaveBeenCalledTimes(1);
  });

  it('connects when answered, counts the time, and says how long it lasted when hung up', async () => {
    const t = setup();
    await ringingOut(t);
    t.calls.frame({ kind: 'accepted', id: ID, c: CHAT, sdp: 'v=0 their answer' });
    await flush();
    expect(t.peers[0]!.remote).toEqual([{ type: 'answer', sdp: 'v=0 their answer' }]);
    expect(t.deps.ring.stop).toHaveBeenCalled();
    expect(t.text()).toContain('Đang kết nối…');
    t.peers[0]!.state('connected');
    await t.pass(65_000);
    expect(t.host.querySelector('[data-call-state]')!.textContent).toBe('1:05');
    await t.press('hang');
    expect(t.api.hangUp).toHaveBeenCalledWith(ID);
    expect(t.text()).toContain('Cuộc gọi đã kết thúc · 1:05');
    expect(t.peers[0]!.closed).toBe(true);
    expect(t.track.stopped).toBe(true);
    expect(t.calls.busy()).toBe(false);
    await t.pass(ENDED_MS);
    expect(t.host.textContent).toBe('');
  });

  it('says "still here" every aliveSeconds while the call is on', async () => {
    const t = setup();
    await ringingOut(t);
    await t.pass(30_000);
    await t.pass(30_000);
    expect(t.api.callAlive).toHaveBeenCalledTimes(2);
    expect(t.api.callAlive).toHaveBeenCalledWith(ID);
  });

  it.each([['missed', 'Không trả lời'], ['declined', 'Cuộc gọi bị từ chối'], ['busy', 'Máy bận'], ['failed', 'Không kết nối được']])(
    'says why a call that never connected ended (%s)', async (outcome, words) => {
      const t = setup();
      await ringingOut(t);
      t.calls.frame({ kind: 'ended', id: ID, c: CHAT, outcome });
      await flush();
      expect(t.text()).toContain(words);
      expect(t.deps.ring.stop).toHaveBeenCalled();
    });

  it('a friend already in a call is "busy" at once', async () => {
    const t = setup({ api: { startCall: vi.fn(async () => ok(info({ state: 'ended', outcome: 'busy' }))) } });
    await ringingOut(t);
    expect(t.text()).toContain('Máy bận');
    expect(t.deps.ring.outgoing).not.toHaveBeenCalled();
  });

  it('a decline that arrives before the call has its id still ends it', async () => {
    let answer!: (v: unknown) => void;
    const t = setup({ api: { startCall: vi.fn(() => new Promise((resolve) => { answer = resolve; })) } });
    const started = t.calls.start(CHAT, { name: 'Mẹ' });
    await flush(); await flush();
    t.peers[0]!.gatherNow();
    await flush();
    t.calls.frame({ kind: 'ended', id: ID, c: CHAT, outcome: 'declined' });
    answer(ok(info()));
    await started;
    expect(t.text()).toContain('Cuộc gọi bị từ chối');
  });

  it('hung up before the server answered: the call is hung up as soon as it has an id', async () => {
    let answer!: (v: unknown) => void;
    const t = setup({ api: { startCall: vi.fn(() => new Promise((resolve) => { answer = resolve; })) } });
    const started = t.calls.start(CHAT, { name: 'Mẹ' });
    await flush(); await flush();
    t.peers[0]!.gatherNow();
    await flush();
    await t.press('hang');
    answer(ok(info()));
    await started;
    expect(t.api.hangUp).toHaveBeenCalledWith(ID);
  });

  it('a refused microphone says how to allow it, calls nobody, and stays until closed', async () => {
    const t = setup({ mic: async () => { throw new DOMException('no', 'NotAllowedError'); } });
    await t.calls.start(CHAT, { name: 'Mẹ' });
    expect(t.text()).toContain('Gom cần micro để gọi');
    expect(t.api.callServers).not.toHaveBeenCalled();
    await t.pass(60_000);
    expect(t.text()).toContain('Gom cần micro để gọi');
    await t.press('close');
    expect(t.host.textContent).toBe('');
  });

  it('a friend with no page open is said in words, and the microphone is let go', async () => {
    const t = setup({ api: { callServers: vi.fn(async () => fail('cannot_take_calls', 409)) } });
    await t.calls.start(CHAT, { name: 'Mẹ' });
    expect(t.text()).toContain('Người này đang không mở Gom');
    expect(t.track.stopped).toBe(true);
    expect(t.api.startCall).not.toHaveBeenCalled();
  });

  it('mute turns the microphone off and says so on the button', async () => {
    const t = setup();
    await ringingOut(t);
    await t.press('mute');
    expect(t.track.enabled).toBe(false);
    expect(t.host.querySelector('[data-call="mute"]')!.getAttribute('aria-pressed')).toBe('true');
    expect(t.host.querySelector('[data-call="mute"]')!.textContent).toContain('Tắt mic');
    await t.press('mute');
    expect(t.track.enabled).toBe(true);
  });

  it('has no speaker button on the web, and asks a phone to keep Gom on screen', async () => {
    const t = setup({ phone: true });
    await ringingOut(t);
    expect(t.host.querySelectorAll('.call-ctl button')).toHaveLength(2);
    expect(t.text()).toContain('Giữ Gom trên màn hình trong lúc gọi.');
  });

  it('a page closing during a call hangs up on its way out', async () => {
    const t = setup();
    await ringingOut(t);
    t.calls.leaving();
    expect(t.api.hangUp).toHaveBeenCalledWith(ID, true);
  });

  it('a lost connection ends the call', async () => {
    const t = setup();
    await ringingOut(t);
    t.calls.frame({ kind: 'accepted', id: ID, c: CHAT, sdp: 'v=0 a' });
    await flush();
    t.peers[0]!.state('failed');
    await flush();
    expect(t.api.hangUp).toHaveBeenCalledWith(ID);
    expect(t.text()).toContain('Không kết nối được');
  });

  it('answered but never connected: gives up after 30 s instead of "connecting" for ever', async () => {
    const t = setup();
    await ringingOut(t);
    t.calls.frame({ kind: 'accepted', id: ID, c: CHAT, sdp: 'v=0 a' });
    await flush();
    t.peers[0]!.state('checking');
    await t.pass(CONNECT_MS - 1000);
    expect(t.text()).toContain('Đang kết nối…');
    expect(t.api.hangUp).not.toHaveBeenCalled();
    await t.pass(1000);
    expect(t.api.hangUp).toHaveBeenCalledWith(ID);
    expect(t.text()).toContain('Không kết nối được');
    expect(t.peers[0]!.closed).toBe(true);
    expect(t.calls.busy()).toBe(false);
  });

  it('a call that connects in time is not given up later', async () => {
    const t = setup();
    await ringingOut(t);
    t.calls.frame({ kind: 'accepted', id: ID, c: CHAT, sdp: 'v=0 a' });
    await flush();
    t.peers[0]!.state('connected');
    await t.pass(CONNECT_MS * 2);
    expect(t.api.hangUp).not.toHaveBeenCalled();
    expect(t.host.querySelector('[data-call-state]')!.textContent).toBe('1:00');
  });
});

describe('calls: a call coming in', () => {
  const incoming = { kind: 'incoming' as const, id: ID, c: CHAT, from: { id: 'u-mom', displayName: 'Mẹ' } };

  it('rings, with the caller in the tab title, and Decline and Answer', async () => {
    const t = setup();
    t.calls.frame(incoming);
    expect(t.deps.ring.incoming).toHaveBeenCalled();
    expect(t.deps.title).toHaveBeenCalledWith('📞 Mẹ đang gọi');
    expect([...t.host.querySelectorAll('.call-ctl button')].map((b) => b.textContent?.trim())).toEqual(['Từ chối', 'Trả lời']);
    expect(document.activeElement?.getAttribute('data-call')).toBe('answer');
    expect(t.calls.busy()).toBe(true);
    // a page closing while it only rings does not hang up: it rings on the person's other pages
    t.calls.leaving();
    expect(t.api.hangUp).not.toHaveBeenCalled();
  });

  it('declining tells the server and quiets the page', async () => {
    const t = setup();
    t.calls.frame(incoming);
    await t.press('hang');
    expect(t.api.declineCall).toHaveBeenCalledWith(ID);
    expect(t.deps.ring.stop).toHaveBeenCalled();
    expect(t.deps.title).toHaveBeenLastCalledWith(null);
    expect(t.host.textContent).toBe('');
  });

  it('answering fetches the offer, answers it with every address, and connects', async () => {
    const t = setup();
    t.calls.frame(incoming);
    await t.press('answer');
    await flush();
    expect(t.api.callServers).toHaveBeenCalledWith({ callId: ID });
    expect(t.peers[0]!.remote).toEqual([{ type: 'offer', sdp: 'v=0 their offer' }]);
    t.peers[0]!.gatherNow();
    await flush(); await flush();
    expect(t.api.acceptCall).toHaveBeenCalledWith(ID, 'v=0 answer+addresses');
    t.peers[0]!.state('connected');
    await flush();
    expect(t.host.querySelector('[data-call-state]')!.textContent).toBe('0:00');
    expect(t.deps.title).toHaveBeenLastCalledWith(null);
  });

  it('answered here but never connected: gives up after 30 s', async () => {
    const t = setup();
    t.calls.frame(incoming);
    await t.press('answer');
    await flush();
    t.peers[0]!.gatherNow();
    await flush(); await flush();
    expect(t.api.acceptCall).toHaveBeenCalled();
    await t.pass(CONNECT_MS - 1000);
    expect(t.api.hangUp).not.toHaveBeenCalled();
    await t.pass(1000);
    expect(t.api.hangUp).toHaveBeenCalledWith(ID);
    expect(t.text()).toContain('Không kết nối được');
  });

  it('answered first on another device: says so', async () => {
    const t = setup({ api: { acceptCall: vi.fn(async () => fail('call_answered', 409)) } });
    t.calls.frame(incoming);
    await t.press('answer');
    await flush();
    t.peers[0]!.gatherNow();
    await flush(); await flush();
    expect(t.text()).toContain('Đã trả lời trên thiết bị khác');
  });

  it('stops ringing when answered elsewhere or when the caller gives up', async () => {
    for (const f of [{ kind: 'answered_elsewhere' as const, id: ID, c: CHAT }, { kind: 'ended' as const, id: ID, c: CHAT, outcome: 'missed' }]) {
      const t = setup();
      t.calls.frame(incoming);
      t.calls.frame(f);
      expect(t.deps.ring.stop).toHaveBeenCalled();
      expect(t.host.textContent).toBe('');
      expect(t.calls.busy()).toBe(false);
    }
  });

  it('a second call while one is on is left to the server ("busy")', async () => {
    const t = setup();
    await ringingOut(t);
    t.calls.frame({ ...incoming, id: 'call-2' });
    expect(t.deps.ring.incoming).not.toHaveBeenCalled();
    expect(t.text()).toContain('Đang đổ chuông…');
  });

  it('frames of another call are ignored', async () => {
    const t = setup();
    t.calls.frame(incoming);
    t.calls.frame({ kind: 'ended', id: 'call-other', c: CHAT, outcome: 'missed' });
    expect(t.calls.busy()).toBe(true);
  });
});

/** A call answered and connected, on the caller's side. */
async function talking(t: ReturnType<typeof setup>) {
  await ringingOut(t);
  t.calls.frame({ kind: 'accepted', id: ID, c: CHAT, sdp: 'v=0 their answer' });
  await flush();
  t.peers[0]!.state('connected');
  await flush();
}

describe('calls: the call bar (mockups 34, 35)', () => {
  it('minimises to a bar with the name and time, which brings the call back; its red button ends it', async () => {
    const t = setup();
    await talking(t);
    await t.pass(5000);
    await t.press('minimise');
    expect(t.host.querySelector('.call-screen')).toBeNull();
    const bar = t.host.querySelector('[data-call-bar]')!;
    expect(bar.querySelector('b')!.textContent).toBe('Mẹ');
    expect(bar.querySelector('span')!.textContent).toBe('· 0:05');
    expect(document.activeElement?.getAttribute('data-call')).toBe('open');
    await t.pass(1000);
    expect(bar.textContent).toContain('0:06');   // the time goes on in the bar
    await t.press('open');
    expect(t.host.querySelector('.call-screen')).not.toBeNull();
    await t.press('minimise');
    await t.press('bar-hang');
    expect(t.api.hangUp).toHaveBeenCalledWith(ID);
    expect(t.text()).toContain('Cuộc gọi đã kết thúc · 0:06');   // the ending is shown full size
  });

  it('shows a crossed mic in the bar when muted, and can minimise while it rings, but not a call coming in', async () => {
    const t = setup();
    await ringingOut(t);
    await t.press('mute');
    await t.press('minimise');
    expect(t.text()).toContain('Đang đổ chuông…');
    expect(t.host.querySelectorAll('[data-call="open"] svg')).toHaveLength(2);   // the handset and the crossed mic
    const u = setup();
    u.calls.frame({ kind: 'incoming', id: ID, c: CHAT, from: { id: 'u-mom', displayName: 'Mẹ' } });
    expect(u.host.querySelector('[data-call="minimise"]')).toBeNull();
  });
});

describe('calls: a browser that does not ring (Settings › Thông báo)', () => {
  it('neither rings nor shows a call coming in, but still makes its own calls', async () => {
    const t = setup({ takesCalls: false });
    t.calls.frame({ kind: 'incoming', id: ID, c: CHAT, from: { id: 'u-mom', displayName: 'Mẹ' } });
    expect(t.deps.ring.incoming).not.toHaveBeenCalled();
    expect(t.host.querySelector('[data-call="answer"]')).toBeNull();
    expect(t.calls.busy()).toBe(false);
    await ringingOut(t);
    expect(t.text()).toContain('Đang đổ chuông…');
  });
});

describe('calls: a network change', () => {
  it('the caller restarts the connection after 2 s down, sends the new offer, takes the answer, and carries on', async () => {
    const t = setup();
    await talking(t);
    const pc = t.peers[0]!;
    pc.state('disconnected');
    await flush();
    expect(t.text()).toContain('Đang kết nối lại…');
    expect(t.text()).toContain('Mạng vừa đổi');
    await t.pass(RECONNECT_AFTER_MS - 1);
    expect(pc.restarts).toBe(0);
    await t.pass(1);
    expect(pc.restarts).toBe(1);
    expect(pc.offers.at(-1)).toEqual({ iceRestart: true });
    pc.gatherNow();
    await flush(); await flush();
    expect(t.api.callSdp).toHaveBeenCalledWith(ID, 'offer', 'v=0 offer again+addresses');
    t.calls.frame({ kind: 'sdp', id: ID, c: CHAT, type: 'answer', sdp: 'v=0 new answer', rev: 1 });
    await flush();
    expect(pc.remote.at(-1)).toEqual({ type: 'answer', sdp: 'v=0 new answer' });
    pc.state('connected');
    await flush();
    expect(t.text()).not.toContain('Đang kết nối lại…');
  });

  it('a connection back on its own within 2 s is not restarted', async () => {
    const t = setup();
    await talking(t);
    t.peers[0]!.state('disconnected');
    t.peers[0]!.state('connected');
    await t.pass(10_000);
    expect(t.peers[0]!.restarts).toBe(0);
  });

  it('the person called never restarts, answers the caller\'s new offer, and ignores an older one', async () => {
    const t = setup();
    t.calls.frame({ kind: 'incoming', id: ID, c: CHAT, from: { id: 'u-mom', displayName: 'Mẹ' } });
    await t.press('answer');
    await flush();
    const pc = t.peers[0]!;
    pc.gatherNow();
    await flush(); await flush();
    pc.state('connected');
    pc.state('disconnected');
    await t.pass(10_000);
    expect(pc.restarts).toBe(0);
    expect(t.text()).toContain('Đang kết nối lại…');
    t.calls.frame({ kind: 'sdp', id: ID, c: CHAT, type: 'offer', sdp: 'v=0 offer again', rev: 2 });
    await flush();
    pc.gatherNow();
    await flush(); await flush();
    expect(pc.remote.at(-1)).toEqual({ type: 'offer', sdp: 'v=0 offer again' });
    expect(t.api.callSdp).toHaveBeenCalledWith(ID, 'answer', 'v=0 answer+addresses');
    t.calls.frame({ kind: 'sdp', id: ID, c: CHAT, type: 'offer', sdp: 'v=0 stale', rev: 1 });
    await flush();
    expect(pc.remote.at(-1)).toEqual({ type: 'offer', sdp: 'v=0 offer again' });
  });

  it('gives up after a minute down', async () => {
    const t = setup();
    await talking(t);
    t.peers[0]!.state('failed');
    await t.pass(GIVE_UP_MS - 1000);
    expect(t.calls.busy()).toBe(true);
    await t.pass(5000);
    expect(t.api.hangUp).toHaveBeenCalledWith(ID);
    expect(t.text()).toContain('Không kết nối được');
  });
});

describe('calls: long calls', () => {
  it('renews the relay login 10 minutes before it runs out, and the caller then restarts so both use the new ones', async () => {
    const t = setup();
    await talking(t);
    const pc = t.peers[0]!;
    const renewAt = (servers.seconds - RENEW_BEFORE_S) * 1000;
    await t.pass(renewAt - 60_000);
    expect(t.api.callServers).toHaveBeenCalledTimes(1);
    await t.pass(60_000);
    expect(t.api.callServers).toHaveBeenLastCalledWith({ callId: ID });
    expect(pc.configs).toEqual([{ iceServers: servers.iceServers }]);
    expect(pc.restarts).toBe(0);
    await t.pass((45 + 15) * 1000);   // after the other side has surely renewed too (it rang at most 45 s)
    expect(pc.restarts).toBe(1);
  });
});
