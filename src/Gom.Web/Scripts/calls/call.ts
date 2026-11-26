import { html, nothing, render, type TemplateResult } from 'lit-html';
import type { Api, Failure } from '../api.js';
import type { CallFrame } from '../chat/live.js';
import { callLength } from '../chat/model.js';
import * as icons from '../icons.js';
import { say } from '../say.js';
import { errorText } from '../text.js';
import { avatar } from '../ui.js';

/**
 * Voice calls in the page (docs/voice-calls.md, section 2b): one call at a time, one-to-one with a friend, while a Gom page that takes
 * calls is open. The steps go to the server as REST and its news comes back on the socket; the sound goes browser to browser. Each
 * offer and answer carries every address (no trickle ICE), so the page waits for them before sending, up to GATHER_MS.
 */

/** The friend on the other end, as the chat shows them. */
export interface CallPeer { name: string; avatar?: string | null }

/** The part of RTCPeerConnection a call uses, so tests can stand in for it. */
export interface PeerLike {
  addTrack(track: MediaStreamTrack, stream: MediaStream): unknown;
  createOffer(): Promise<RTCSessionDescriptionInit>;
  createAnswer(): Promise<RTCSessionDescriptionInit>;
  setLocalDescription(description: RTCSessionDescriptionInit): Promise<void>;
  setRemoteDescription(description: RTCSessionDescriptionInit): Promise<void>;
  readonly localDescription: { sdp: string } | null;
  readonly iceGatheringState: string;
  readonly connectionState: string;
  onicegatheringstatechange: (() => void) | null;
  onconnectionstatechange: (() => void) | null;
  ontrack: ((e: { streams: readonly MediaStream[] }) => void) | null;
  close(): void;
}

export type CallApi = Pick<Api, 'callServers' | 'startCall' | 'getCall' | 'acceptCall' | 'declineCall' | 'hangUp' | 'callAlive'>;

export interface CallDeps {
  api: CallApi;
  /** Where the call screen is drawn (an element of its own, over the page). */
  host: HTMLElement;
  peer(config: RTCConfiguration): PeerLike;
  /** The microphone; rejects when the person refuses it or there is none. */
  mic(): Promise<MediaStream>;
  /** Plays the other side's sound. */
  play(stream: MediaStream | null): void;
  ring: { incoming(): void; outgoing(): void; stop(): void };
  /** The tab's title while a call rings here; null puts the page's own back. */
  title(text: string | null): void;
  /** While true, leaving the page asks first (the browser's own question). */
  guardLeaving(on: boolean): void;
  /** A phone's browser: the call screen asks to keep Gom on screen, since a page in the background loses the microphone. */
  phone(): boolean;
  /** Milliseconds, any steady clock. */
  now(): number;
  timer: { set(fn: () => void, ms: number): number; clear(id: number): void };
  newId(): string;
}

export interface Calls {
  /** Calls the friend of this one-to-one chat. Does nothing while a call is on. */
  start(conversation: string, peer: CallPeer): Promise<void>;
  /** A call frame from the socket. */
  frame(frame: CallFrame): void;
  /** True while a call is on (ringing or connected): the page must not reload by itself. */
  busy(): boolean;
  /** The page is closing: a call it is in is hung up on the way out, so the other side is not left waiting. */
  leaving(): void;
}

/** How long an offer or answer waits for its addresses before it goes with what it has (agreed with H: 3 s). */
export const GATHER_MS = 3000;
/** How long the "ended" screen stays before it closes by itself (mockup 28). */
export const ENDED_MS = 2500;

type Phase = 'outgoing' | 'incoming' | 'connecting' | 'active' | 'ended';

interface Current {
  id: string | null;
  conversation: string;
  peer: CallPeer;
  mine: boolean;
  phase: Phase;
  /** The line under the name; while connected, the time is drawn instead. */
  status: string;
  /** An ending that needs reading (a refused microphone, a call that could not be made): stays until closed. */
  problem: boolean;
  muted: boolean;
  connectedAt: number | null;
  pc: PeerLike | null;
  stream: MediaStream | null;
  timers: number[];
  /** Hung up before the server gave the call its id: hung up as soon as it does. */
  cancelled: boolean;
}

export function createCalls(deps: CallDeps): Calls {
  let cur: Current | null = null;
  /** Calls the server said had ended before this page knew their id (a decline faster than the start's answer). */
  const endedEarly = new Map<string, string>();

  const live = (c: Current | null): c is Current => c !== null && c === cur && c.phase !== 'ended';

  function draw(): void { render(cur === null ? nothing : screen(cur), deps.host); }

  function every(c: Current, fn: () => void, ms: number): void {
    const tick = () => { if (!live(c)) return; fn(); c.timers.push(deps.timer.set(tick, ms)); };
    c.timers.push(deps.timer.set(tick, ms));
  }

  /** Lets go of everything the call holds: the connection, the microphone, the sound, the timers. */
  function release(c: Current): void {
    for (const t of c.timers) deps.timer.clear(t);
    c.timers = [];
    c.pc?.close();
    c.pc = null;
    for (const track of c.stream?.getTracks() ?? []) track.stop();
    c.stream = null;
    deps.play(null);
    deps.ring.stop();
    deps.title(null);
    deps.guardLeaving(false);
  }

  /** The call is over here: says how, then closes (by itself, or when closed if it needs reading). An empty text closes at once. */
  function finish(c: Current, text: string, problem = false): void {
    if (c !== cur || c.phase === 'ended') return;
    release(c);
    if (text === '') { cur = null; draw(); return; }
    c.phase = 'ended';
    c.status = text;
    c.problem = problem;
    if (!problem) c.timers.push(deps.timer.set(() => close(c), ENDED_MS));
    draw();
  }

  function close(c: Current): void {
    if (c !== cur) return;
    release(c);
    cur = null;
    draw();
  }

  function failed(c: Current, failure: Failure): void { finish(c, errorText(failure), true); }

  /** What the server's outcome means to the one reading it. The person called hears nothing of a call that never rang through. */
  function endingText(c: Current, outcome: string): string {
    if (c.connectedAt !== null) return say.calls.endedAfter(callLength(Math.round((deps.now() - c.connectedAt) / 1000)));
    if (outcome === 'failed') return say.calls.failed;
    if (!c.mine) return '';
    return outcome === 'missed' ? say.calls.noAnswer : outcome === 'declined' ? say.calls.declinedByThem : outcome === 'busy' ? say.calls.busy : say.calls.ended;
  }

  async function microphone(c: Current): Promise<MediaStream | null> {
    try {
      const stream = await deps.mic();
      if (!live(c)) { for (const t of stream.getTracks()) t.stop(); return null; }
      return stream;
    } catch {
      finish(c, say.calls.needMic, true);
      return null;
    }
  }

  /** The connection with the microphone on it; its sound is played as it comes. */
  function connect(c: Current, iceServers: RTCIceServer[], stream: MediaStream): PeerLike {
    const pc = deps.peer({ iceServers });
    c.pc = pc;
    c.stream = stream;
    for (const track of stream.getAudioTracks()) pc.addTrack(track, stream);
    pc.ontrack = (e) => { if (live(c)) deps.play(e.streams[0] ?? null); };
    pc.onconnectionstatechange = () => {
      if (!live(c)) return;
      if (pc.connectionState === 'connected' && c.phase !== 'active') {
        c.phase = 'active';
        c.connectedAt ??= deps.now();
        every(c, draw, 1000);
        draw();
      } else if (pc.connectionState === 'failed') {
        if (c.id !== null) void deps.api.hangUp(c.id);
        finish(c, c.connectedAt === null ? say.calls.failed : endingText(c, 'completed'));
      }
    };
    return pc;
  }

  /** Every address in it, or what there is after GATHER_MS. */
  function gathered(pc: PeerLike): Promise<string> {
    return new Promise((resolve) => {
      const done = () => { pc.onicegatheringstatechange = null; deps.timer.clear(t); resolve(pc.localDescription?.sdp ?? ''); };
      const t = deps.timer.set(done, GATHER_MS);
      if (pc.iceGatheringState === 'complete') { done(); return; }
      pc.onicegatheringstatechange = () => { if (pc.iceGatheringState === 'complete') done(); };
    });
  }

  function keepAlive(c: Current, seconds: number): void {
    every(c, () => {
      if (c.id === null) return;
      void deps.api.callAlive(c.id).then((r) => { if (!r.ok && r.error === 'call_ended') finish(c, endingText(c, 'completed')); });
    }, Math.max(1, seconds) * 1000);
  }

  function begin(c: Current): void {
    cur = c;
    draw();
    (deps.host.querySelector<HTMLElement>('[data-call-first]'))?.focus();
  }

  async function start(conversation: string, peer: CallPeer): Promise<void> {
    if (cur !== null) return;
    const c: Current = { id: null, conversation, peer, mine: true, phase: 'outgoing', status: say.calls.calling, problem: false, muted: false,
      connectedAt: null, pc: null, stream: null, timers: [], cancelled: false };
    begin(c);
    deps.guardLeaving(true);
    const stream = await microphone(c);
    if (stream === null) return;
    const servers = await deps.api.callServers({ conversationId: conversation });
    if (!live(c)) { for (const t of stream.getTracks()) t.stop(); return; }
    if (!servers.ok) { for (const t of stream.getTracks()) t.stop(); failed(c, servers); return; }
    const pc = connect(c, servers.data.iceServers, stream);
    await pc.setLocalDescription(await pc.createOffer());
    const offer = await gathered(pc);
    if (!live(c)) return;
    const started = await deps.api.startCall(conversation, deps.newId(), offer);
    if (!started.ok) { if (live(c)) failed(c, started); return; }
    const call = started.data;
    if (c.cancelled || !live(c)) { void deps.api.hangUp(call.id); return; }
    c.id = call.id;
    const early = endedEarly.get(call.id);
    if (call.state === 'ended' || early !== undefined) { finish(c, endingText(c, call.outcome ?? early ?? 'failed')); return; }
    c.status = say.calls.ringing;
    deps.ring.outgoing();
    keepAlive(c, call.aliveSeconds);
    draw();
  }

  async function answer(c: Current): Promise<void> {
    if (!live(c) || c.phase !== 'incoming' || c.id === null) return;
    const id = c.id;
    c.phase = 'connecting';
    c.status = say.calls.connecting;
    deps.ring.stop();
    deps.title(null);
    deps.guardLeaving(true);
    draw();
    const stream = await microphone(c);
    if (stream === null) { void deps.api.declineCall(id); return; }
    const [servers, info] = await Promise.all([deps.api.callServers({ callId: id }), deps.api.getCall(id)]);
    if (!live(c)) { for (const t of stream.getTracks()) t.stop(); return; }
    if (!servers.ok || !info.ok) { for (const t of stream.getTracks()) t.stop(); failed(c, !servers.ok ? servers : info as Failure); return; }
    if (info.data.offer === null) { for (const t of stream.getTracks()) t.stop(); finish(c, info.data.state === 'ended' ? '' : say.calls.failed); return; }
    const pc = connect(c, servers.data.iceServers, stream);
    await pc.setRemoteDescription({ type: 'offer', sdp: info.data.offer });
    await pc.setLocalDescription(await pc.createAnswer());
    const sdp = await gathered(pc);
    if (!live(c)) return;
    const accepted = await deps.api.acceptCall(id, sdp);
    if (!accepted.ok) {
      if (!live(c)) return;
      if (accepted.error === 'call_answered') finish(c, say.calls.answeredElsewhere);
      else if (accepted.error === 'call_ended') finish(c, '');
      else failed(c, accepted);
      return;
    }
    keepAlive(c, accepted.data.aliveSeconds);
  }

  function hangUp(c: Current): void {
    if (!live(c)) return;
    if (c.phase === 'incoming') { if (c.id !== null) void deps.api.declineCall(c.id); finish(c, ''); return; }
    if (c.id === null) c.cancelled = true;
    else void deps.api.hangUp(c.id);
    finish(c, endingText(c, 'completed'));
  }

  function toggleMute(c: Current): void {
    c.muted = !c.muted;
    for (const track of c.stream?.getAudioTracks() ?? []) track.enabled = !c.muted;
    draw();
  }

  const button = (cls: string, icon: TemplateResult, label: string, onClick: () => void, extra: { first?: boolean; pressed?: boolean } = {}) => html`
    <button class="rb ${cls}" type="button" data-call=${cls} ?data-call-first=${extra.first === true}
      aria-pressed=${extra.pressed === undefined ? nothing : extra.pressed ? 'true' : 'false'} @click=${onClick}>
      <span class="rb-i">${icon}</span><span>${label}</span></button>`;

  function controls(c: Current): TemplateResult {
    if (c.phase === 'incoming') return html`<div class="call-ctl wide">
      ${button('hang', icons.call(), say.calls.decline, () => hangUp(c))}
      ${button('answer', icons.call(), say.calls.answer, () => void answer(c), { first: true })}</div>`;
    if (c.phase === 'ended') return c.problem
      ? html`<div class="call-ctl">${button('close', icons.close(), say.calls.close, () => close(c), { first: true })}</div>`
      : html``;
    // The web cannot choose the speaker: its place stays empty, so the end button keeps the middle (mockups 31, 32).
    return html`<div class="call-ctl">
      <span class="rb blank" aria-hidden="true"></span>
      ${button('hang', icons.call(), say.calls.end, () => hangUp(c), { first: true })}
      ${button('mute', c.muted ? icons.micOff() : icons.mic(), say.calls.mute, () => toggleMute(c), { pressed: c.muted })}</div>`;
  }

  function screen(c: Current): TemplateResult {
    const status = c.phase === 'active' && c.connectedAt !== null ? callLength(Math.round((deps.now() - c.connectedAt) / 1000)) : c.status;
    return html`<div class="call-screen" role="dialog" aria-modal="true" aria-label=${c.phase === 'incoming' ? say.calls.incoming : say.calls.voiceCall} data-phase=${c.phase}>
      <div class="call-box">
        <p class="call-app">${icons.logo()}<span>${say.calls.gomCall}</span></p>
        <div class="call-who">${avatar(c.peer.name, false, true, c.peer.avatar)}
          <h2>${c.peer.name}</h2>
          <p class="call-state ${c.problem ? 'problem' : ''}" role=${c.problem ? 'alert' : 'status'} data-call-state>${status}</p></div>
        <div class="call-grow"></div>
        ${deps.phone() && (c.phase === 'active' || c.phase === 'connecting' || c.phase === 'outgoing') ? html`<p class="call-note">${say.calls.keepOpen}</p>` : nothing}
        ${controls(c)}
      </div></div>`;
  }

  return {
    start,
    frame(f) {
      const c = cur;
      if (f.kind === 'incoming') {
        if (c !== null) return;   // the server makes a second call "busy" for the person already in one
        const next: Current = { id: f.id, conversation: f.c, peer: { name: f.from.displayName }, mine: false, phase: 'incoming', status: say.calls.incoming,
          problem: false, muted: false, connectedAt: null, pc: null, stream: null, timers: [], cancelled: false };
        begin(next);
        deps.ring.incoming();
        deps.title(say.calls.tabRinging(f.from.displayName));
        return;
      }
      if (c === null || c.id !== f.id) {
        if (f.kind === 'ended' && c?.mine && c.id === null) endedEarly.set(f.id, f.outcome);
        return;
      }
      if (f.kind === 'accepted' && c.mine && live(c) && c.pc !== null) {
        deps.ring.stop();
        c.phase = 'connecting';
        c.status = say.calls.connecting;
        draw();
        void c.pc.setRemoteDescription({ type: 'answer', sdp: f.sdp });
      } else if (f.kind === 'answered_elsewhere' && c.phase === 'incoming') finish(c, '');
      else if (f.kind === 'ended') finish(c, endingText(c, f.outcome));
    },
    busy: () => cur !== null && cur.phase !== 'ended',
    leaving() {
      const c = cur;
      if (c === null || c.id === null || c.phase === 'incoming' || c.phase === 'ended') return;
      void deps.api.hangUp(c.id, true);
    },
  };
}
