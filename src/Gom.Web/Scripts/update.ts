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
  /** Tells the person a new version is there. */
  announce(): void;
}

/** How often a page that stays open asks. */
const EVERY_MS = 10 * 60 * 1000;
/** A tab shown again asks at once, unless it asked less than this long ago. */
const MIN_GAP_MS = 60 * 1000;

/**
 * Notices a new version being deployed while this page is open. The page asks about its own script: the first answer is
 * what it is running, and a later answer that differs (a new ETag) or a 404 (a file that has gone) means a new version.
 * Not being able to ask (offline, a server error) says nothing. It asks once, again every ten minutes, and when the tab is shown
 * again; once it has spoken it stops. It never reloads anything itself: a reload would lose what is being typed.
 */
export function watchForUpdate(deps: UpdateDeps): { check(): Promise<void> } {
  let baseline: string | null | undefined;
  let lastAt = Number.NEGATIVE_INFINITY;
  let announced = false;
  let asking = false;

  async function check(): Promise<void> {
    if (announced || asking) return;
    asking = true;
    lastAt = deps.now();
    try {
      const answer = await deps.probe();
      if (answer === null) return;
      if (answer.status === 404) { announced = true; deps.announce(); return; }
      if (answer.status < 200 || answer.status >= 300) return;
      if (baseline === undefined) { baseline = answer.etag; return; }
      if (baseline !== null && answer.etag !== null && answer.etag !== baseline) { announced = true; deps.announce(); }
    } finally {
      asking = false;
    }
  }

  void check();
  deps.every(() => { void check(); }, EVERY_MS);
  deps.onShown(() => { if (deps.now() - lastAt >= MIN_GAP_MS) void check(); });
  return { check };
}

/** The bar at the top of the page: a new version is there, reload when it suits, or leave it. */
export function showUpdateBar(host: HTMLElement, reload: () => void): void {
  const close = (): void => { render(nothing, host); };
  render(html`
    <div class="updatebar" role="status" data-update>
      <span>${say.app.newVersion}</span>
      <button class="btn small" type="button" data-update-reload @click=${reload}>${say.app.reload}</button>
      <button class="btn secondary small" type="button" data-update-later @click=${close}>${say.chat.later}</button>
    </div>`, host);
}
