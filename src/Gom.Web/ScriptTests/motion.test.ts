import { afterEach, describe, expect, it, vi } from 'vitest';
import { dismissMenu, leave, leaveInfo, leaveThread } from '../Scripts/motion.js';

/** An element that animates the way a browser would: `finish()` ends the last animation started on it. */
function animated() {
  const el = document.createElement('div');
  const runs: Array<{ frames: Keyframe[]; ms: number; cancel: ReturnType<typeof vi.fn>; end: () => void }> = [];
  (el as unknown as { animate: unknown }).animate = (frames: Keyframe[], o: KeyframeAnimationOptions) => {
    let end!: () => void;
    const finished = new Promise<void>((resolve) => { end = resolve; });
    const run = { frames, ms: Number(o.duration), cancel: vi.fn(), end };
    runs.push(run);
    return { finished, cancel: run.cancel };
  };
  return { el, runs, finish: async () => { runs[runs.length - 1]!.end(); await Promise.resolve(); await Promise.resolve(); } };
}

/** matchMedia answering yes to the queries given. */
const media = (...yes: string[]) => vi.spyOn(window, 'matchMedia').mockImplementation((q: string) => ({ matches: yes.includes(q), media: q } as MediaQueryList));
afterEach(() => { vi.restoreAllMocks(); });

describe('moving out', () => {
  it('runs what comes next only once the animation has ended, then lets the animation go', async () => {
    media();
    const a = animated();
    const done = vi.fn();
    leave(a.el, [{ opacity: 1 }, { opacity: 0 }], 200, done);
    expect(done).not.toHaveBeenCalled();
    expect(a.runs[0]!.ms).toBe(200);
    await a.finish();
    expect(done).toHaveBeenCalledTimes(1);
    expect(a.runs[0]!.cancel).toHaveBeenCalled();   // a kept element is not left where the animation ended
  });

  it('goes at once when the device asks for less motion, or the browser cannot animate', () => {
    media('(prefers-reduced-motion: reduce)');
    const a = animated();
    const done = vi.fn();
    leave(a.el, [], 200, done);
    expect(done).toHaveBeenCalledTimes(1);
    expect(a.runs).toHaveLength(0);
    vi.restoreAllMocks(); media();
    const plain = vi.fn();
    leave(document.createElement('div'), [], 200, plain);
    expect(plain).toHaveBeenCalledTimes(1);
  });

  it('slides a dismissed sheet down on a phone, and shrinks a menu on a computer', async () => {
    const scope = document.createElement('div');
    const shade = animated(); shade.el.className = 'scrim';
    const pop = animated(); pop.el.className = 'menu';
    scope.append(shade.el, pop.el);
    media();   // a phone: no mouse
    dismissMenu(scope, vi.fn());
    expect(pop.runs[0]!.frames[1]).toEqual({ transform: 'translateY(100%)' });
    expect(shade.runs[0]!.frames[1]).toEqual({ opacity: 0 });
    vi.restoreAllMocks(); media('(hover: hover) and (pointer: fine) and (min-width: 600px)');
    const done = vi.fn();
    dismissMenu(scope, done);
    expect(pop.runs[1]!.frames[1]).toEqual({ opacity: 0, transform: 'scale(.96)' });
    await pop.finish();
    expect(done).toHaveBeenCalledTimes(1);
  });

  it('slides a phone\'s conversation out to the right, and goes at once on a wide screen', async () => {
    media('(max-width: 899.98px)');
    const pane = animated();
    const done = vi.fn();
    leaveThread(pane.el, done);
    expect(pane.runs[0]!.frames[1]).toEqual({ transform: 'translateX(40%)', opacity: 0 });
    await pane.finish();
    expect(done).toHaveBeenCalledTimes(1);
    vi.restoreAllMocks(); media();
    const wide = vi.fn();
    leaveThread(animated().el, wide);
    expect(wide).toHaveBeenCalledTimes(1);
  });
});

describe('the information panel closing', () => {
  it('slides out to the right and fades on a wide screen, then closes', async () => {
    media('(min-width: 1200px)');
    const panel = animated();
    const done = vi.fn();
    leaveInfo(panel.el, done);
    expect(panel.runs[0]!.frames).toEqual([{ transform: 'none', opacity: 1 }, { transform: 'translateX(24px)', opacity: 0 }]);
    expect(panel.runs[0]!.ms).toBe(160);
    expect(done).not.toHaveBeenCalled();
    await panel.finish();
    expect(done).toHaveBeenCalledTimes(1);
  });

  it('closes at once where it is not a panel beside the conversation', () => {
    media();
    const panel = animated();
    const done = vi.fn();
    leaveInfo(panel.el, done);
    expect(panel.runs).toHaveLength(0);
    expect(done).toHaveBeenCalledTimes(1);
  });
});
