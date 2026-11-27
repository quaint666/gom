/**
 * Things that leave the screen, moving out instead of vanishing: a menu dismissed, a phone's conversation going back to the list. What
 * comes in is CSS's (gom.css, under prefers-reduced-motion: no-preference). None of it runs when the device asks for less motion, nor
 * where the browser cannot animate an element (then it goes at once, as before).
 */

export const motionAllowed = (): boolean => typeof matchMedia !== 'function' || !matchMedia('(prefers-reduced-motion: reduce)').matches;

/**
 * Plays `frames` on `el` for `ms`, then runs `done`, which draws the page without it. The animation is then let go, so an element
 * kept and shown again later (a phone's thread pane) is not left where it ended.
 */
export function leave(el: Element | null, frames: Keyframe[], ms: number, done: () => void): void {
  const target = el as (HTMLElement & { animate?: HTMLElement['animate'] }) | null;
  if (target === null || typeof target.animate !== 'function' || !motionAllowed()) { done(); return; }
  const animation = target.animate(frames, { duration: ms, easing: 'cubic-bezier(.4, 0, 1, 1)', fill: 'forwards' });
  let ran = false;
  const finish = () => { if (ran) return; ran = true; done(); animation.cancel(); };
  animation.finished.then(finish, finish);
}

/**
 * A menu dismissed (Escape, a tap on its shade, "Đóng"): on a phone its sheet slides down, on a computer it shrinks a little and fades,
 * and the shade fades with it; then `done` closes it. `scope` holds the menu and its shade.
 */
export function dismissMenu(scope: ParentNode | null, done: () => void): void {
  const pop = scope?.querySelector('.menu, .gearmenu') ?? null;
  const shade = scope?.querySelector<HTMLElement>('.scrim') ?? null;
  const sheet = typeof matchMedia === 'function' && !matchMedia('(hover: hover) and (pointer: fine) and (min-width: 600px)').matches && pop?.classList.contains('menu') === true;
  if (shade !== null && pop !== null && typeof shade.animate === 'function' && motionAllowed()) shade.animate([{ opacity: 1 }, { opacity: 0 }], { duration: sheet ? 180 : 100, fill: 'forwards' });
  leave(pop, sheet ? [{ transform: 'none' }, { transform: 'translateY(100%)' }] : [{ opacity: 1, transform: 'none' }, { opacity: 0, transform: 'scale(.96)' }], sheet ? 180 : 100, done);
}

/** The information panel beside a conversation, closed on a wide screen: it slides out to the right and fades, as it slid in. */
export function leaveInfo(panel: Element | null, done: () => void): void {
  if (typeof matchMedia !== 'function' || !matchMedia('(min-width: 1200px)').matches) { done(); return; }
  leave(panel, [{ transform: 'none', opacity: 1 }, { transform: 'translateX(24px)', opacity: 0 }], 160, done);
}

/** A phone's conversation going back to the list: it slides out to the right, as it slid in. Elsewhere it goes at once. */
export function leaveThread(pane: Element | null, done: () => void): void {
  if (typeof matchMedia !== 'function' || !matchMedia('(max-width: 899.98px)').matches) { done(); return; }
  leave(pane, [{ transform: 'none', opacity: 1 }, { transform: 'translateX(40%)', opacity: 0 }], 200, done);
}
