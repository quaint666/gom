import { beforeEach, describe, expect, it, vi } from 'vitest';
import { showUpdateBar, watchForUpdate, type Probe, type UpdateDeps } from '../Scripts/update.js';
import { flush } from './helpers.js';

let clock: number;
let answers: Array<Probe | null | 'wait'>;
let released: Array<(p: Probe | null) => void>;
let tick: () => void;
let shown: () => void;
let announce: ReturnType<typeof vi.fn>;
let probe: ReturnType<typeof vi.fn>;

function start(): ReturnType<typeof watchForUpdate> {
  const deps: UpdateDeps = {
    probe: probe as never,
    now: () => clock,
    every: (fn) => { tick = fn; },
    onShown: (fn) => { shown = fn; },
    announce: announce as never,
  };
  return watchForUpdate(deps);
}

const said = (etag: string | null, status = 200): Probe => ({ status, etag });

beforeEach(() => {
  clock = 0;
  answers = [];
  released = [];
  announce = vi.fn();
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

describe('the bar', () => {
  it('offers to reload, and does not reload by itself', () => {
    const host = document.createElement('div');
    const reload = vi.fn();
    showUpdateBar(host, reload);
    expect(host.querySelector('[data-update]')!.getAttribute('role')).toBe('status');
    expect(host.textContent).toContain('Có bản mới');
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
