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
  createOffer(options?: RTCOfferOptions): Promise<RTCSessionDescriptionInit>;
  createAnswer(): Promise<RTCSessionDescriptionInit>;
  setLocalDescription(description: RTCSessionDescriptionInit): Promise<void>;
  setRemoteDescription(description: RTCSessionDescriptionInit): Promise<void>;
  readonly localDescription: { sdp: string } | null;
  readonly iceGatheringState: string;
  readonly connectionState: string;
  readonly signalingState: string;
  setConfiguration(config: RTCConfiguration): void;
  restartIce?(): void;
  onicegatheringstatechange: (() => void) | null;
  onconnectionstatechange: (() => void) | null;
  ontrack: ((e: { streams: readonly MediaStream[] }) => void) | null;
  close(): void;
}

export type CallApi = Pick<Api, 'callServers' | 'startCall' | 'getCall' | 'acceptCall' | 'declineCall' | 'hangUp' | 'callSdp' | 'callAlive'>;

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
  /** Whether this browser rings for a call (`calls/here.ts`); when not, a call to you rings elsewhere and is not shown here. */
  takesCalls(): boolean;
  /** Milliseconds, any steady clock. */
  now(): number;
  timer: { set(fn: () => void, ms: number): number; clear(id: number): void };
  newId(): string;
}

export interface Calls {
  /** Whether a call can be made from here now (always on a page; in the Android app, once the app has said it takes calls). */
  ready(): boolean;
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
/** A connection down this long after a network change is restarted by the caller (agreed with H: about 2 s), then again this often. */
export const RECONNECT_AFTER_MS = 2000;
export const RECONNECT_EVERY_MS = 5000;
/** Down this long, the call is given up. */
export const GIVE_UP_MS = 60_000;
/** Answered but never connected this long (a network that lets nothing through): the call is given up, never left "connecting". */
export const CONNECT_MS = 30_000;
/** A relay login is renewed this long before it runs out (3 h logins: at 2 h 50 min; decision 22). */
export const RENEW_BEFORE_S = 600;

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
  /** Shrunk to the bar at the top of the chat page (mockups 34, 35). */
  minimised: boolean;
  /** When the connection went down (a network change), while it is down. */
  downSince: number | null;
  /** The newest mid-call offer or answer taken, so an older one arriving late is ignored. */
  rev: number;
  ringSeconds: number;
}

export function createCalls(deps: CallDeps): Calls {
  let cur: Current | null = null;
  /** Calls the server said had ended before this page knew their id (a decline faster than the start's answer). */
  const endedEarly = new Map<string, string>();

  const live = (c: Current | null): c is Current => c !== null && c === cur && c.phase !== 'ended';

  function draw(): void { render(cur === null ? nothing : cur.minimised ? bar(cur) : screen(cur), deps.host); }

  /** Shrinks the call to the bar, or brings it back, and puts the focus on what was opened. */
  function minimise(c: Current, on: boolean): void {
    c.minimised = on;
    draw();
    deps.host.querySelector<HTMLElement>(on ? '[data-call="open"]' : '[data-call-first]')?.focus();
  }

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
    c.minimised = false;
    c.downSince = null;
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
      const state = pc.connectionState;
      if (state === 'connected') {
        c.downSince = null;
        if (c.phase !== 'active') {
          c.phase = 'active';
          c.connectedAt ??= deps.now();
          every(c, draw, 1000);
        }
        draw();
      } else if ((state === 'disconnected' || state === 'failed') && c.phase === 'active') {
        if (c.downSince !== null) return;
        c.downSince = deps.now();
        c.timers.push(deps.timer.set(() => down(c), RECONNECT_AFTER_MS));
        draw();
      } else if (state === 'failed') {
        if (c.id !== null) void deps.api.hangUp(c.id);
        finish(c, say.calls.failed);
      }
    };
    return pc;
  }

  /**
   * The connection is still down: the caller restarts it (only the caller, so the two never offer at once), again every
   * RECONNECT_EVERY_MS, and either side gives up after GIVE_UP_MS.
   */
  function down(c: Current): void {
    if (!live(c) || c.downSince === null) return;
    if (deps.now() - c.downSince >= GIVE_UP_MS) {
      if (c.id !== null) void deps.api.hangUp(c.id);
      finish(c, say.calls.failed);
      return;
    }
    if (c.mine) void restart(c);
    c.timers.push(deps.timer.set(() => down(c), RECONNECT_EVERY_MS));
  }

  /** A new offer that gathers every address again (an ICE restart), sent through the server; the answer comes as a frame. */
  async function restart(c: Current): Promise<void> {
    const pc = c.pc;
    if (!live(c) || pc === null || c.id === null || pc.signalingState !== 'stable') return;
    pc.restartIce?.();
    await pc.setLocalDescription(await pc.createOffer({ iceRestart: true }));
    const sdp = await gathered(pc, true);
    if (live(c) && c.id !== null) await deps.api.callSdp(c.id, 'offer', sdp);
  }

  /** A mid-call offer (the caller restarted) is answered; an answer (to my restart) is taken. Older ones are ignored. */
  async function renegotiate(c: Current, type: 'offer' | 'answer', sdp: string, rev: number): Promise<void> {
    const pc = c.pc;
    if (!live(c) || pc === null || c.id === null || rev <= c.rev) return;
    c.rev = rev;
    if (type === 'answer') {
      if (c.mine && pc.signalingState === 'have-local-offer') await pc.setRemoteDescription({ type, sdp });
      return;
    }
    if (c.mine) return;
    await pc.setRemoteDescription({ type, sdp });
    await pc.setLocalDescription(await pc.createAnswer());
    const answer = await gathered(pc, true);
    if (live(c) && c.id !== null) await deps.api.callSdp(c.id, 'answer', answer);
  }

  /**
   * Fresh relay logins before the old ones run out (decision 22). Each side renews its own; the caller then restarts the connection
   * once the other side has surely renewed too (it got its login at most the ringing time later), so the restart uses both.
   */
  function renewLater(c: Current, seconds: number): void {
    const wait = Math.max(seconds / 2, seconds - RENEW_BEFORE_S) * 1000;
    c.timers.push(deps.timer.set(() => void renew(c), wait));
  }

  async function renew(c: Current): Promise<void> {
    if (!live(c) || c.id === null || c.pc === null) return;
    const servers = await deps.api.callServers({ callId: c.id });
    if (!live(c) || c.pc === null) return;
    if (!servers.ok) { c.timers.push(deps.timer.set(() => void renew(c), 60_000)); return; }
    c.pc.setConfiguration({ iceServers: servers.data.iceServers });
    renewLater(c, servers.data.seconds);
    if (c.mine) c.timers.push(deps.timer.set(() => void restart(c), (c.ringSeconds + 15) * 1000));
  }

  /** Every address in it, or what there is after GATHER_MS. After a restart a "complete" left from before does not count. */
  function gathered(pc: PeerLike, again = false): Promise<string> {
    return new Promise((resolve) => {
      const done = () => { pc.onicegatheringstatechange = null; deps.timer.clear(t); resolve(pc.localDescription?.sdp ?? ''); };
      const t = deps.timer.set(done, GATHER_MS);
      if (!again && pc.iceGatheringState === 'complete') { done(); return; }
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
      connectedAt: null, pc: null, stream: null, timers: [], cancelled: false, minimised: false, downSince: null, rev: 0, ringSeconds: 45 };
    begin(c);
    deps.guardLeaving(true);
    const stream = await microphone(c);
    if (stream === null) return;
    const servers = await deps.api.callServers({ conversationId: conversation });
    if (!live(c)) { for (const t of stream.getTracks()) t.stop(); return; }
    if (!servers.ok) { for (const t of stream.getTracks()) t.stop(); failed(c, servers); return; }
    const pc = connect(c, servers.data.iceServers, stream);
    renewLater(c, servers.data.seconds);
    await pc.setLocalDescription(await pc.createOffer());
    const offer = await gathered(pc);
    if (!live(c)) return;
    const started = await deps.api.startCall(conversation, deps.newId(), offer);
    if (!started.ok) { if (live(c)) failed(c, started); return; }
    const call = started.data;
    if (c.cancelled || !live(c)) { void deps.api.hangUp(call.id); return; }
    c.id = call.id;
    c.ringSeconds = call.ringSeconds;
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
    renewLater(c, servers.data.seconds);
    await pc.setRemoteDescription({ type: 'offer', sdp: info.data.offer });
    await pc.setLocalDescription(await pc.createAnswer());
    const sdp = await gathered(pc);
    if (!live(c)) return;
    deadline(c);
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

  /** Answered: the connection has CONNECT_MS to come up, else the call ends as one that could not connect. */
  function deadline(c: Current): void {
    c.timers.push(deps.timer.set(() => {
      if (!live(c) || c.phase !== 'connecting') return;
      if (c.id !== null) void deps.api.hangUp(c.id);
      finish(c, say.calls.failed);
    }, CONNECT_MS));
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

  /** The line under the name: the time while connected, "reconnecting" while the connection is down, else what is happening. */
  const statusOf = (c: Current): string => c.downSince !== null ? say.calls.reconnecting
    : c.phase === 'active' && c.connectedAt !== null ? callLength(Math.round((deps.now() - c.connectedAt) / 1000)) : c.status;

  const going = (c: Current): boolean => c.phase === 'active' || c.phase === 'connecting' || c.phase === 'outgoing';

  function screen(c: Current): TemplateResult {
    const down = c.downSince !== null;
    return html`<div class="call-screen" role="dialog" aria-modal="true" aria-label=${c.phase === 'incoming' ? say.calls.incoming : say.calls.voiceCall} data-phase=${c.phase}>
      <div class="call-box">
        ${going(c) ? html`<button class="call-min" type="button" data-call="minimise" aria-label=${say.calls.minimise} @click=${() => minimise(c, true)}>${icons.chevronDown()}</button>` : nothing}
        <p class="call-app">${icons.logo()}<span>${say.calls.gomCall}</span></p>
        <div class="call-who">${avatar(c.peer.name, false, true, c.peer.avatar)}
          <h2>${c.peer.name}</h2>
          <p class="call-state ${c.problem ? 'problem' : ''} ${down ? 'warn' : ''}" role=${c.problem ? 'alert' : 'status'} data-call-state>${down ? icons.wifiOff() : nothing}${statusOf(c)}</p></div>
        <div class="call-grow"></div>
        ${down ? html`<p class="call-note">${say.calls.reconnectNote}</p>`
          : deps.phone() && going(c) ? html`<p class="call-note">${say.calls.keepOpen}</p>` : nothing}
        ${controls(c)}
      </div></div>`;
  }

  /** The call minimised: a bar across the top of the chat page; a tap brings the call back, the red button ends it (mockups 34, 35). */
  function bar(c: Current): TemplateResult {
    return html`<div class="call-bar" data-call-bar>
      <button class="cb-open" type="button" data-call="open" aria-label=${say.calls.backTo(c.peer.name)} @click=${() => minimise(c, false)}>
        ${icons.call()}<b>${c.peer.name}</b><span>· ${statusOf(c)}</span>${c.muted ? icons.micOff() : nothing}</button>
      <button class="cb-end" type="button" data-call="bar-hang" aria-label=${say.calls.end} @click=${() => hangUp(c)}>${icons.call()}</button>
    </div>`;
  }

  return {
    ready: () => true,
    start,
    frame(f) {
      const c = cur;
      if (f.kind === 'incoming') {
        if (c !== null) return;   // the server makes a second call "busy" for the person already in one
        if (!deps.takesCalls()) return;   // this browser does not ring: the app, or another device, takes the call
        const next: Current = { id: f.id, conversation: f.c, peer: { name: f.from.displayName }, mine: false, phase: 'incoming', status: say.calls.incoming,
          problem: false, muted: false, connectedAt: null, pc: null, stream: null, timers: [], cancelled: false, minimised: false, downSince: null, rev: 0, ringSeconds: 45 };
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
        deadline(c);
        draw();
        void c.pc.setRemoteDescription({ type: 'answer', sdp: f.sdp });
      } else if (f.kind === 'sdp') void renegotiate(c, f.type, f.sdp, f.rev);
      else if (f.kind === 'answered_elsewhere' && c.phase === 'incoming') finish(c, '');
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
