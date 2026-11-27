import { html, render } from 'lit-html';
import type { Api, Failure } from '../api.js';
import { brand, codeField, errorBox, idField, passwordField } from '../ui.js';
import { say } from '../say.js';

/** Where to go after signing in: a path on this site, never another address. */
export function safeNext(next: string | null): string {
  return next !== null && /^\/(?![/\\])[^\s\\]*$/.test(next) ? next : '/';
}

export function mountSignIn(root: HTMLElement, api: Api, go: (path: string) => void, next: string | null = null): void {
  let gomId = '';
  let password = '';
  let shown = false;
  let busy = false;
  let error: Failure | null = null;
  /** Whether the owner lets people ask to join; until the server says, the invite-only line. */
  let open = false;
  /** Two-step sign-in: the password was right and the code is asked for; the password is sent again with it. */
  let asking = false;
  let code = '';

  const codeStep = () => html`
    <div class="page">
      ${brand()}
      <h1 class="center">${say.account.twoStepTitle}</h1>
      <form @submit=${onSubmit} data-code-step>
        ${errorBox(error)}
        <p class="lead">${say.account.twoStepAsk}</p>
        ${codeField({ value: code, onInput: (v) => { code = v; } })}
        <button class="btn" type="submit" ?disabled=${busy}>${say.account.next}</button>
        <button class="btn secondary" type="button" data-code-back @click=${() => { asking = false; code = ''; password = ''; error = null; update(); }}>${say.account.back}</button>
      </form>
      <p class="foot">${say.account.twoStepLost}</p>
    </div>`;

  const view = () => asking ? codeStep() : html`
    <div class="page">
      ${brand()}
      <h1 class="center">${say.account.signIn}</h1>
      <form @submit=${onSubmit}>
        ${errorBox(error)}
        ${idField({ value: gomId, enterkeyhint: 'next', onInput: (v) => { gomId = v; } })}
        ${passwordField({ label: say.account.password, autocomplete: 'current-password', value: password, shown,
          onInput: (v) => { password = v; }, onToggle: () => { shown = !shown; update(); } })}
        <button class="btn" type="submit" ?disabled=${busy}>${say.account.signIn}</button>
      </form>
      <p class="foot" data-sign-in-foot>${open ? html`${say.account.noAccount} <a href="/sign-up" data-ask-to-join>${say.account.askToJoin}</a>` : say.account.invitedOnly}</p>
      <p class="foot"><a href="/privacy" data-policy-link>${say.account.policy}</a></p>
    </div>`;
  const update = () => render(view(), root);

  async function onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    if (busy) return;
    busy = true; error = null; update();
    const result = asking ? await api.signIn(gomId, password, code) : await api.signIn(gomId, password);
    if (result.ok) { go(safeNext(next)); return; }
    busy = false;
    if (!result.ok && result.error === 'code_required') { asking = true; error = null; update(); root.querySelector<HTMLInputElement>('#code')?.focus(); return; }
    if (asking) code = '';
    error = result; update();
    if (asking) root.querySelector<HTMLInputElement>('#code')?.focus();
  }

  update();
  void api.signUpOpen().then((result) => { if (result.ok && result.data.open) { open = true; update(); } });
}
