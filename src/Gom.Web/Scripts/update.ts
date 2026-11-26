import { html, nothing, render } from 'lit-html';
import { say } from './say.js';

/** What a HEAD request for the page's own script answered: the status and its ETag (null when there is none). */
export interface Probe { status: number; etag: string | null }

export interface UpdateDeps {
  /** Asks the server about the script this page runs from; null when there is no answer (no connection). */
  probe(): Promise<Probe | null>;
  now(): number;
  /** Calls fn every `ms` for as long as the page lives. */
  every(fn: () => void, ms: number): void;
  /** Calls fn each time the tab is shown again. */
  onShown(fn: () => void): void;
  /** Calls fn each time the tab is hidden (another tab, another app, the screen off). */
  onHidden(fn: () => void): void;
  visible(): boolean;
  /** True while a reload would lose something: files waiting to be sent, a message being changed, a field being filled in. */
  busy(): boolean;
  /**
   * Tells the person a new version is there (again, when the tab is shown and it has not reloaded yet). `must`: the server no
   * longer answers this page (it is older than the server's minimum), so there is no "later".
   */
  announce(must: boolean): void;
  reload(): void;
  /** When this tab last reloaded by itself, kept across that reload; null when it never has. */
  reloadedAt: { get(): number | null; set(at: number): void };
  /** The version this page runs (its script's fingerprint); null when it has none, and then only the probe can tell. */
  own: string | null;
}

/** How often a page that stays open asks. */
const EVERY_MS = 10 * 60 * 1000;
/** A tab shown again asks at once, unless it asked less than this long ago. */
const MIN_GAP_MS = 60 * 1000;
/** A tab that reloaded by itself this recently does not do it again: if the new page still looks old, the bar asks instead of a loop. */
const AUTO_GAP_MS = 5 * 60 * 1000;

/**
 * Notices a new version being deployed while this page is open, and moves to it when that loses nothing.
 *
 * Two ways to notice. The live connection (chat pages) is told the server's version each time it connects, and a deploy restarts
 * the server, so the page hears within seconds (`heard`). Every page also asks about its own script: the first answer is what it
 * is running, and a later answer that differs (a new ETag) or a 404 (a file that has gone) means a new version. Not being able to
 * ask (offline, a server error) says nothing. It asks once, again every ten minutes, and when the tab is shown again.
 *
 * Once it knows, it reloads while the tab is hidden, or as the tab is shown again, unless something would be lost (`busy`); a
 * tab in view shows the bar instead, and the bar comes back each time the tab is shown until the reload happens.
 */
export function watchForUpdate(deps: UpdateDeps): { check(): Promise<void>; heard(version: string): void; tooOld(): void } {
  let baseline: string | null | undefined;
  let lastAt = Number.NEGATIVE_INFINITY;
  let stale = false;
  let must = false;
  let asking = false;

  /** Reloads if that loses nothing and this tab has not just done so; says whether it did. */
  function reloadQuietly(): boolean {
    if (deps.busy()) return false;
    const last = deps.reloadedAt.get();
    if (last !== null && deps.now() - last < AUTO_GAP_MS) return false;
    deps.reloadedAt.set(deps.now());
    deps.reload();
    return true;
  }

  function found(): void {
    if (stale) return;
    stale = true;
    if (deps.visible() || !reloadQuietly()) deps.announce(must);
  }

  async function check(): Promise<void> {
    if (stale || asking) return;
    asking = true;
    lastAt = deps.now();
    try {
      const answer = await deps.probe();
      if (answer === null) return;
      if (answer.status === 404) { found(); return; }
      if (answer.status < 200 || answer.status >= 300) return;
      if (baseline === undefined) { baseline = answer.etag; return; }
      if (baseline !== null && answer.etag !== null && answer.etag !== baseline) found();
    } finally {
      asking = false;
    }
  }

  void check();
  deps.every(() => { void check(); }, EVERY_MS);
  deps.onHidden(() => { if (stale) reloadQuietly(); });
  deps.onShown(() => {
    if (stale) { if (!reloadQuietly()) deps.announce(must); return; }
    if (deps.now() - lastAt >= MIN_GAP_MS) void check();
  });
  return {
    check,
    heard(version) { if (deps.own !== null && version !== deps.own) found(); },
    /** The server refused this page as too old: the same moves, and the bar without "later" (shown again if it was the gentle one). */
    tooOld() {
      if (must) return;
      must = true;
      if (stale) deps.announce(true); else found();
    },
  };
}

/** The version in this page's script name (main.<version>.js), or null for a plain name. */
export function versionOf(scriptPath: string): string | null {
  return /\/main\.([0-9a-f]+)\.js$/.exec(scriptPath)?.[1] ?? null;
}

/**
 * The bar at the top of the page: a new version is there, reload now, or leave it until the tab is shown again (or reloads by
 * itself). `must`: this page can no longer work with the server, so reloading is the only choice.
 */
export function showUpdateBar(host: HTMLElement, reload: () => void, must = false): void {
  const close = (): void => { render(nothing, host); };
  render(must
    ? html`
      <div class="updatebar" role="alert" data-update data-update-must>
        <span><b>${say.app.updated}</b><small>${say.app.reloadToGoOn}</small></span>
        <button class="btn small" type="button" data-update-reload @click=${reload}>${say.app.reload}</button>
      </div>`
    : html`
      <div class="updatebar" role="status" data-update>
        <span><b>${say.app.newVersion}</b><small>${say.app.reloadsItself}</small></span>
        <button class="btn small" type="button" data-update-reload @click=${reload}>${say.app.reload}</button>
        <button class="btn secondary small" type="button" data-update-later @click=${close}>${say.chat.later}</button>
      </div>`, host);
}
