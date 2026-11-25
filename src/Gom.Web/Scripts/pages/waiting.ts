import { html, nothing, render } from 'lit-html';
import type { Api, Failure, User } from '../api.js';
import { brand, errorBox } from '../ui.js';
import { say } from '../say.js';

/**
 * All someone who asked to join sees until the owner answers: waiting (check again, sign out) or refused (take the request back,
 * sign out). It checks again by itself when the person comes back to the page, never on a timer.
 */
export function mountWaiting(root: HTMLElement, api: Api, go: (path: string) => void, me: User, onShown: (check: () => void) => void): void {
  let standing = me.standing ?? 'pending';
  let status = '';
  let asking = false;
  let busy = false;
  let error: Failure | null = null;

  const view = () => html`
    <div class="page center" data-waiting=${standing}>
      ${brand()}
      ${errorBox(error)}
      ${standing === 'refused' ? html`
        <div><h1>${say.waiting.refused}</h1><p class="lead">${say.waiting.refusedNote}</p></div>
        ${asking ? html`
          <div class="card" role="group" aria-labelledby="withdraw-q">
            <p id="withdraw-q"><b>${say.waiting.withdrawQ}</b></p>
            <button class="btn danger" type="button" data-withdraw-yes ?disabled=${busy} @click=${() => void withdraw()}>${say.waiting.withdraw}</button>
            <button class="btn secondary" type="button" data-withdraw-no @click=${() => { asking = false; update(); }}>${say.common.cancel}</button>
          </div>`
          : html`<button class="btn secondary" type="button" data-withdraw @click=${() => { asking = true; update(); }}>${say.waiting.withdraw}</button>`}`
        : html`
        <div><h1>${say.waiting.sent}</h1><p class="lead">${say.waiting.sentNote}</p></div>
        <button class="btn" type="button" data-check ?disabled=${busy} @click=${() => void check()}>${say.waiting.check}</button>
        ${status === '' ? nothing : html`<p class="hint" role="status" data-waiting-status>${status}</p>`}`}
      <button class="btn secondary" type="button" data-signout ?disabled=${busy} @click=${() => void signOut()}>${say.common.signOut}</button>
    </div>`;
  const update = () => render(view(), root);

  /** Approved: the whole app, from its start. Still waiting: says so. Refused meanwhile: shows that. */
  async function check(): Promise<void> {
    busy = true; error = null; update();
    const result = await api.me();
    busy = false;
    if (!result.ok) { if (result.error === 'unauthorized') { go('/sign-in'); return; } error = result; update(); return; }
    if ((result.data.standing ?? 'member') === 'member') { go('/'); return; }
    standing = result.data.standing ?? 'pending';
    status = say.waiting.stillWaiting;
    update();
  }

  async function withdraw(): Promise<void> {
    busy = true; update();
    const result = await api.withdrawSignUp();
    busy = false;
    if (result.ok) { go('/sign-in'); return; }
    error = result; update();
  }

  async function signOut(): Promise<void> {
    busy = true; update();
    await api.signOut();
    go('/sign-in');
  }

  onShown(() => void check());
  update();
}
