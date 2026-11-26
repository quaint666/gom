import { beforeEach, describe, expect, it, vi } from 'vitest';
import { showUpdateBar, versionOf, watchForUpdate, type Probe, type UpdateDeps } from '../Scripts/update.js';
import { flush } from './helpers.js';

let clock: number;
let answers: Array<Probe | null | 'wait'>;
let released: Array<(p: Probe | null) => void>;
let tick: () => void;
let shown: () => void;
let hidden: () => void;
let announce: ReturnType<typeof vi.fn>;
let reload: ReturnType<typeof vi.fn>;
let probe: ReturnType<typeof vi.fn>;
let visible: boolean;
let busy: boolean;
let reloadedAt: number | null;

function start(own: string | null = 'aaa'): ReturnType<typeof watchForUpdate> {
  const deps: UpdateDeps = {
    probe: probe as never,
    now: () => clock,
    every: (fn) => { tick = fn; },
    onShown: (fn) => { shown = fn; },
    onHidden: (fn) => { hidden = fn; },
    visible: () => visible,
    busy: () => busy,
    announce: announce as never,
    reload: reload as never,
    reloadedAt: { get: () => reloadedAt, set: (at) => { reloadedAt = at; } },
    own,
  };
  return watchForUpdate(deps);
}
const hide = () => { visible = false; hidden(); };
const show = () => { visible = true; shown(); };

const said = (etag: string | null, status = 200): Probe => ({ status, etag });

beforeEach(() => {
  clock = 0;
  answers = [];
  released = [];
  announce = vi.fn();
  reload = vi.fn();
  visible = true;
  busy = false;
  reloadedAt = null;
  probe = vi.fn(() => {
    const next = answers.shift();
    if (next === 'wait') return new Promise((resolve) => released.push(resolve));
    return Promise.resolve(next ?? null);
  });
});

describe('noticing a new version', () => {
  it('asks once at the start, and takes the first answer as the version it is running', async () => {
    answers = [said('"a"')];
    start();
    await flush();
    expect(probe).toHaveBeenCalledTimes(1);
    expect(announce).not.toHaveBeenCalled();
  });

  it('says nothing while the answer stays the same', async () => {
    answers = [said('"a"'), said('"a"'), said('"a"')];
    start();
    await flush();
    tick(); await flush();
    tick(); await flush();
    expect(probe).toHaveBeenCalledTimes(3);
    expect(announce).not.toHaveBeenCalled();
  });

  it('says so when the ETag has changed, and then stops asking', async () => {
    answers = [said('"a"'), said('"b"'), said('"c"')];
    start();
    await flush();
    tick(); await flush();
    expect(announce).toHaveBeenCalledTimes(1);
    tick(); await flush();
    clock += 10 * 60_000; shown(); await flush();
    expect(probe).toHaveBeenCalledTimes(2);
    expect(announce).toHaveBeenCalledTimes(1);
  });

  it('says so when the script is no longer there', async () => {
    answers = [said('"a"'), { status: 404, etag: null }];
    start();
    await flush();
    tick(); await flush();
    expect(announce).toHaveBeenCalledTimes(1);
  });

  it('says nothing when it cannot ask or the server errs, and tries again later', async () => {
    answers = [said('"a"'), null, said('"error-page"', 500), said('"a"'), said('"b"')];
    start();
    await flush();
    tick(); await flush();   // no answer (offline)
    tick(); await flush();   // a server error
    tick(); await flush();   // the same again
    expect(announce).not.toHaveBeenCalled();
    tick(); await flush();
    expect(announce).toHaveBeenCalledTimes(1);
  });

  it('cannot tell anything when the first answer has no ETag, and says nothing', async () => {
    answers = [said(null), said('"b"'), said('"c"')];
    start();
    await flush();
    tick(); await flush();
    tick(); await flush();
    expect(announce).not.toHaveBeenCalled();
  });

  it('does not ask while a question is still waiting for its answer', async () => {
    answers = ['wait'];
    start();
    tick(); tick(); shown();
    expect(probe).toHaveBeenCalledTimes(1);
    released[0]!(said('"a"'));
    await flush();
  });

  it('asks again when the tab is shown, but not within a minute of the last question', async () => {
    answers = [said('"a"'), said('"a"'), said('"a"')];
    start();
    await flush();
    clock += 30_000; shown(); await flush();
    expect(probe).toHaveBeenCalledTimes(1);
    clock += 30_000; shown(); await flush();
    expect(probe).toHaveBeenCalledTimes(2);
    shown(); await flush();   // right after: too soon
    expect(probe).toHaveBeenCalledTimes(2);
  });
});

describe('hearing the version over the live connection', () => {
  it('knows at once when the server hands out another version, and says so on a tab in view', async () => {
    answers = [said('"a"')];
    const w = start('aaa');
    await flush();
    w.heard('aaa');
    expect(announce).not.toHaveBeenCalled();
    w.heard('bbb');
    expect(announce).toHaveBeenCalledTimes(1);
    expect(reload).not.toHaveBeenCalled();
    tick(); await flush();
    expect(probe).toHaveBeenCalledTimes(1);   // knowing already, it stops asking
  });

  it('cannot compare without a version of its own (plain names), and leaves it to the probe', async () => {
    answers = [said('"a"')];
    const w = start(null);
    await flush();
    w.heard('bbb');
    expect(announce).not.toHaveBeenCalled();
  });
});

describe('moving to the new version', () => {
  it('reloads once the tab is hidden', async () => {
    answers = [said('"a"')];
    const w = start();
    await flush();
    w.heard('bbb');
    hide();
    expect(reload).toHaveBeenCalledTimes(1);
    expect(reloadedAt).toBe(clock);
  });

  it('reloads at once when it finds out while the tab is hidden', async () => {
    answers = [said('"a"'), said('"b"')];
    start();
    await flush();
    visible = false;
    tick(); await flush();
    expect(reload).toHaveBeenCalledTimes(1);
    expect(announce).not.toHaveBeenCalled();
  });

  it('reloads as the tab is shown again, when it could not while hidden', async () => {
    answers = [said('"a"')];
    const w = start();
    await flush();
    w.heard('bbb');
    busy = true;
    hide();
    expect(reload).not.toHaveBeenCalled();
    busy = false;
    show();
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('never reloads while something would be lost, and brings the bar back each time the tab is shown', async () => {
    answers = [said('"a"')];
    const w = start();
    await flush();
    busy = true;
    w.heard('bbb');
    hide(); show(); hide(); show();
    expect(reload).not.toHaveBeenCalled();
    expect(announce).toHaveBeenCalledTimes(3);
  });

  it('does not reload by itself twice within a few minutes: the bar asks instead of a loop', async () => {
    answers = [said('"a"')];
    reloadedAt = -60_000;   // this tab reloaded by itself a minute ago
    const w = start();
    await flush();
    w.heard('bbb');
    hide();
    expect(reload).not.toHaveBeenCalled();
    show();
    expect(announce).toHaveBeenCalledTimes(2);
    clock += 5 * 60_000;
    hide();
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('reads the version from the script name', () => {
    expect(versionOf('/js/main.bf066b7453.js')).toBe('bf066b7453');
    expect(versionOf('/js/main.js')).toBeNull();
  });
});

describe('the bar', () => {
  it('offers to reload, and does not reload by itself', () => {
    const host = document.createElement('div');
    const reload = vi.fn();
    showUpdateBar(host, reload);
    expect(host.querySelector('[data-update]')!.getAttribute('role')).toBe('status');
    expect(host.textContent).toContain('Có bản mới');
    expect(host.textContent).toContain('Gom tự tải lại khi bạn chuyển sang việc khác.');
    expect(reload).not.toHaveBeenCalled();
    host.querySelector<HTMLButtonElement>('[data-update-reload]')!.click();
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('goes away on "later" without reloading', () => {
    const host = document.createElement('div');
    const reload = vi.fn();
    showUpdateBar(host, reload);
    host.querySelector<HTMLButtonElement>('[data-update-later]')!.click();
    expect(host.querySelector('[data-update]')).toBeNull();
    expect(reload).not.toHaveBeenCalled();
  });
});
