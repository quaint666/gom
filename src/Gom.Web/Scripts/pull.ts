import { html, nothing, render } from 'lit-html';
import { reload as reloadIcon } from './icons.js';
import { say } from './say.js';

/** How far the circle has to come down, in CSS pixels, before letting go reloads. */
export const PULL_READY = 70;
/** The circle moves half as far as the finger, so a pull feels held back, as in other apps. */
const RESISTANCE = 0.5;
/** How long "not reloaded" stays on screen. */
const REFUSAL_MS = 4000;

/** How far the circle comes down for a finger that moved from `startY` to `y` (never above its start). */
export const pulled = (startY: number, y: number): number => Math.max(0, (y - startY) * RESISTANCE);

/**
 * Whether a touch on `target` may start a pull: inside the chat list, with the list alone on screen (a phone; on a wide screen the
 * thread is beside it) and already at its top. In a thread, pulling down at the top is how older messages come, so it never starts
 * there. `shown` says whether an element is laid out (the stylesheet decides which panes a width shows).
 */
export function canPullList(target: EventTarget | null, root: ParentNode, shown: (el: Element) => boolean): boolean {
  const pane = target instanceof Element ? target.closest('.list-pane') : null;
  if (pane === null) return false;
  const thread = root.querySelector('.thread-pane');
  if (thread !== null && shown(thread)) return false;
  return (pane.querySelector('.items')?.scrollTop ?? 0) <= 0;
}

export interface PullDeps {
  /** Where the touches are heard (the page's root). */
  area: HTMLElement;
  /** Where the circle and the "not reloaded" line are drawn. */
  host: HTMLElement;
  canStart(target: EventTarget | null): boolean;
  /** True while a reload would lose something: files waiting to be sent, a message being changed. */
  busy(): boolean;
  reload(): void;
  later(fn: () => void, ms: number): void;
}

/**
 * Pull down to reload: a finger that drags the list down from its top brings a circle with it; let go once it is far enough
 * (`PULL_READY`) and the page reloads, as "Tải lại" on the update bar does (typed words are kept). When that would lose something
 * it says so and stays. A finger that first moves up or sideways is scrolling, not pulling.
 */
export function attachPull(deps: PullDeps): void {
  let start: { x: number; y: number } | null = null;
  let decided = false;
  let distance = 0;
  let refusals = 0;

  const draw = (state: 'idle' | 'pulling' | 'reloading' | 'refused'): void => {
    const ready = distance >= PULL_READY || state === 'reloading';
    render(state === 'idle' ? nothing : state === 'refused'
      ? html`<div class="toast" role="status" data-pull-refused><span>${say.chat.pullBusy}</span></div>`
      : html`<div class="pull" data-pull data-ready=${ready ? 'true' : 'false'} aria-hidden="true">
          <span class="pull-dot">${reloadIcon()}</span>
          ${ready ? html`<span class="pull-label">${state === 'reloading' ? say.chat.pullReloading : say.chat.pullRelease}</span>` : nothing}
        </div>`, deps.host);
    // The page's policy allows no style attribute: where the circle is goes through custom properties set from script, as the
    // row menu's position does.
    const pull = deps.host.querySelector<HTMLElement>('.pull');
    if (pull !== null) {
      pull.style.setProperty('--pull-y', `${Math.round(state === 'reloading' ? PULL_READY : Math.min(distance, PULL_READY * 1.5))}px`);
      pull.style.setProperty('--pull-turn', `${Math.round(Math.min(distance / PULL_READY, 1) * 270)}deg`);
    }
  };

  const stop = (): void => { start = null; decided = false; distance = 0; };

  deps.area.addEventListener('touchstart', (e) => {
    stop();
    if (e.touches.length !== 1 || !deps.canStart(e.target)) return;
    const t = e.touches[0]!;
    start = { x: t.clientX, y: t.clientY };
  }, { passive: true });

  deps.area.addEventListener('touchmove', (e) => {
    if (start === null) return;
    const t = e.touches[0];
    if (e.touches.length !== 1 || t === undefined) { stop(); draw('idle'); return; }
    const dx = t.clientX - start.x;
    const dy = t.clientY - start.y;
    if (!decided) {
      if (Math.abs(dx) < 4 && Math.abs(dy) < 4) return;
      // Up is scrolling the list, sideways is something else: leave both alone for the rest of this touch.
      if (dy <= 0 || Math.abs(dx) > dy) { stop(); return; }
      decided = true;
    }
    if (e.cancelable) e.preventDefault();
    distance = pulled(start.y, t.clientY);
    draw('pulling');
  }, { passive: false });

  const end = (cancelled: boolean): void => {
    if (start === null) return;
    const go = !cancelled && decided && distance >= PULL_READY;
    stop();
    if (!go) { draw('idle'); return; }
    if (deps.busy()) {
      const mine = ++refusals;
      draw('refused');
      deps.later(() => { if (mine === refusals) draw('idle'); }, REFUSAL_MS);
      return;
    }
    draw('reloading');
    deps.reload();
  };
  deps.area.addEventListener('touchend', () => end(false));
  deps.area.addEventListener('touchcancel', () => end(true));
}
