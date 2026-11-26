import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PULL_READY, attachPull, canPullList, pulled } from '../Scripts/pull.js';

let area: HTMLElement;
let host: HTMLElement;
let reload: ReturnType<typeof vi.fn>;
let busy: boolean;
let allowed: boolean;
let pending: Array<() => void>;

/** A touch event at one point (jsdom has no Touch constructor: the list is set by hand). */
function touch(type: string, y: number, x = 100, target: EventTarget = area): Event {
  const e = new Event(type, { bubbles: true, cancelable: true });
  const points = type === 'touchend' || type === 'touchcancel' ? [] : [{ clientX: x, clientY: y }];
  Object.defineProperty(e, 'touches', { value: points });
  target.dispatchEvent(e);
  return e;
}

/** A finger put down at 100 and moved down by `by` pixels in small steps, then lifted. */
function drag(by: number, lift = true): void {
  touch('touchstart', 100);
  for (let moved = 10; moved <= by; moved += 10) touch('touchmove', 100 + moved);
  if (by % 10 !== 0) touch('touchmove', 100 + by);
  if (lift) touch('touchend', 0);
}

beforeEach(() => {
  document.body.innerHTML = '<div id="area"></div><div id="host"></div>';
  area = document.getElementById('area')!;
  host = document.getElementById('host')!;
  reload = vi.fn();
  busy = false;
  allowed = true;
  pending = [];
  attachPull({ area, host, canStart: () => allowed, busy: () => busy, reload: reload as never, later: (fn) => { pending.push(fn); } });
});

describe('how far a pull goes', () => {
  it('moves the circle half as far as the finger, never above its start', () => {
    expect(pulled(100, 240)).toBe(70);
    expect(pulled(100, 50)).toBe(0);
  });
});

describe('pulling the list down', () => {
  it('reloads when let go far enough down (the finger moves twice the distance)', () => {
    drag(PULL_READY * 2);
    expect(reload).toHaveBeenCalledTimes(1);
    expect(host.querySelector('[data-pull]')?.textContent).toContain('Đang tải lại');
  });

  it('does nothing when let go short of it, and the circle goes away', () => {
    drag(PULL_READY * 2 - 2, false);
    expect(host.querySelector('[data-pull]')?.getAttribute('data-ready')).toBe('false');
    touch('touchend', 0);
    expect(reload).not.toHaveBeenCalled();
    expect(host.querySelector('[data-pull]')).toBeNull();
  });

  it('says "release to reload" once far enough, before the finger is lifted', () => {
    drag(PULL_READY * 2, false);
    expect(host.querySelector('[data-pull]')?.getAttribute('data-ready')).toBe('true');
    expect(host.textContent).toContain('Thả để tải lại');
  });

  it('keeps the page from scrolling while pulling', () => {
    touch('touchstart', 100);
    expect(touch('touchmove', 140).defaultPrevented).toBe(true);
  });

  it('leaves a touch alone when it may not start here (a thread, a wide screen, a list scrolled down)', () => {
    allowed = false;
    touch('touchstart', 100);
    expect(touch('touchmove', 300).defaultPrevented).toBe(false);
    touch('touchend', 0);
    expect(reload).not.toHaveBeenCalled();
  });

  it('is scrolling, not pulling, when the finger first goes up, even if it comes back down', () => {
    touch('touchstart', 100);
    expect(touch('touchmove', 80).defaultPrevented).toBe(false);
    touch('touchmove', 400);
    touch('touchend', 0);
    expect(reload).not.toHaveBeenCalled();
  });

  it('is not a pull when the finger first goes sideways', () => {
    touch('touchstart', 100, 100);
    touch('touchmove', 110, 140);
    touch('touchmove', 400, 140);
    touch('touchend', 0);
    expect(reload).not.toHaveBeenCalled();
  });

  it('does not reload when the touch is cancelled', () => {
    drag(PULL_READY * 2, false);
    touch('touchcancel', 0);
    expect(reload).not.toHaveBeenCalled();
  });
});

describe('when a reload would lose something', () => {
  it('says so instead, then the line goes away', () => {
    busy = true;
    drag(PULL_READY * 2);
    expect(reload).not.toHaveBeenCalled();
    expect(host.querySelector('[data-pull-refused]')?.textContent).toContain('Chưa tải lại');
    pending.forEach((fn) => fn());
    expect(host.querySelector('[data-pull-refused]')).toBeNull();
  });
});

describe('where a pull may start', () => {
  const page = (scrolled: number, threadShown: boolean): { root: HTMLElement; row: Element; shown: (el: Element) => boolean } => {
    document.body.innerHTML = `<div id="root"><section class="list-pane"><div class="items"><button class="item">A</button></div></section>
      <section class="thread-pane"><p class="in-thread">t</p></section></div>`;
    const root = document.getElementById('root')!;
    root.querySelector('.items')!.scrollTop = scrolled;
    return { root, row: root.querySelector('.item')!, shown: (el) => !el.classList.contains('thread-pane') || threadShown };
  };

  it('starts in the list at its top on a phone', () => {
    const { root, row, shown } = page(0, false);
    expect(canPullList(row, root, shown)).toBe(true);
  });

  it('not with the thread beside the list (a wide screen)', () => {
    const { root, row, shown } = page(0, true);
    expect(canPullList(row, root, shown)).toBe(false);
  });

  it('not in a thread', () => {
    const { root, shown } = page(0, false);
    expect(canPullList(root.querySelector('.in-thread'), root, shown)).toBe(false);
  });

  it('not when the list is scrolled down', () => {
    const { root, row, shown } = page(120, false);
    expect(canPullList(row, root, shown)).toBe(false);
  });
});
