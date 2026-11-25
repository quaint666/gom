import { html, render } from 'lit-html';
import type { Api, Failure } from '../api.js';
import { brand, errorBox, idField, passwordField } from '../ui.js';
import { limits } from '../limits.js';
import { passwordHint } from '../text.js';
import { say } from '../say.js';

type State = 'loading' | 'closed' | 'form';

/** Asking to join, when the owner allows it: the join form plus who you are; then the waiting screen at "/". */
export function mountSignUp(root: HTMLElement, api: Api, go: (path: string) => void): void {
  let state: State = 'loading';
  let displayName = '';
  let gomId = '';
  let password = '';
  let note = '';
  let shown = false;
  let busy = false;
  let error: Failure | null = null;

  const view = () => {
    if (state === 'loading') return html`<div class="page">${brand()}</div>`;
    if (state === 'closed') return html`
      <div class="page center" data-sign-up-closed>
        ${brand()}
        <div><h1>${say.signUp.closed}</h1><p class="lead">${say.signUp.closedNote}</p></div>
        <a class="btn secondary" href="/sign-in">${say.account.signIn}</a>
      </div>`;
    return html`
      <div class="page">
        ${brand()}
        <div><h1>${say.signUp.title}</h1><p class="lead">${say.signUp.lead}</p></div>
        <form @submit=${onSubmit} data-sign-up>
          ${errorBox(error)}
          <div class="field">
            <label for="display-name">${say.account.displayName}</label>
            <div class="input"><input id="display-name" name="displayName" type="text" autocomplete="nickname" enterkeyhint="next"
              maxlength=${limits().nameChars} .value=${displayName} @input=${(e: Event) => { displayName = (e.target as HTMLInputElement).value; }}></div>
            <p class="hint">${say.account.displayNameNote}</p>
          </div>
          ${idField({ value: gomId, enterkeyhint: 'next', onInput: (v) => { gomId = v; },
            hint: say.account.gomIdHint(limits().gomIdMin, limits().gomIdMax) })}
          ${passwordField({ label: say.account.password, autocomplete: 'new-password', value: password, shown, hint: passwordHint(),
            onInput: (v) => { password = v; }, onToggle: () => { shown = !shown; update(); } })}
          <div class="field">
            <label for="note">${say.signUp.note}</label>
            <div class="input"><textarea id="note" name="note" rows="2" required maxlength=${limits().noteChars}
              .value=${note} @input=${(e: Event) => { note = (e.target as HTMLTextAreaElement).value; }}></textarea></div>
            <p class="hint">${say.signUp.noteHint}</p>
          </div>
          <button class="btn" type="submit" ?disabled=${busy}>${say.signUp.send}</button>
        </form>
        <p class="foot">${say.account.haveAccount} <a href="/sign-in">${say.account.signIn}</a></p>
      </div>`;
  };
  const update = () => render(view(), root);

  async function onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    if (busy) return;
    busy = true; error = null; update();
    const result = await api.signUp({ gomId, password, displayName, note });
    if (result.ok) { go('/'); return; }
    busy = false;
    if (result.error === 'sign_up_closed') state = 'closed'; else error = result;
    update();
  }

  update();
  void api.signUpOpen().then((result) => {
    state = result.ok && result.data.open ? 'form' : 'closed';
    update();
  });
}
