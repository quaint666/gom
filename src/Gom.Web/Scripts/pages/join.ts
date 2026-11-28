import { html, render } from 'lit-html';
import type { Api, Failure } from '../api.js';
import { brand, deadLink, errorBox, idField, passwordField } from '../ui.js';
import * as icons from '../icons.js';
import { limits } from '../limits.js';
import { passwordHint } from '../text.js';
import { say } from '../say.js';

type State = { kind: 'loading' } | { kind: 'dead' } | { kind: 'form'; invitedBy: string };

export function mountJoin(root: HTMLElement, api: Api, go: (path: string) => void, token: string): void {
  let state: State = { kind: 'loading' };
  let displayName = '';
  let gomId = '';
  let password = '';
  let shown = false;
  let busy = false;
  let error: Failure | null = null;

  const view = () => {
    if (state.kind === 'dead') return deadLink(say.account.deadInvite, say.account.deadInviteNote);
    if (state.kind === 'loading') return html`<div class="page">${brand()}</div>`;
    return html`
      <div class="page">
        ${brand()}
        <div><h1>${say.account.invited}</h1><p class="lead">${say.account.about}</p></div>
        <div class="note">${icons.logo()}<div><b>${state.invitedBy}</b> ${say.account.invitesYou}</div></div>
        <form @submit=${onSubmit}>
          ${errorBox(error)}
          <div class="field">
            <label for="display-name">${say.account.displayName}</label>
            <div class="input"><input id="display-name" name="displayName" type="text" autocomplete="nickname" enterkeyhint="next"
              .value=${displayName} @input=${(e: Event) => { displayName = (e.target as HTMLInputElement).value; }}></div>
            <p class="hint">${say.account.displayNameNote}</p>
          </div>
          ${idField({ value: gomId, enterkeyhint: 'next', onInput: (v) => { gomId = v; },
            hint: say.account.gomIdHint(limits().gomIdMin, limits().gomIdMax) })}
          ${passwordField({ label: say.account.password, autocomplete: 'new-password', value: password, shown, hint: passwordHint(),
            onInput: (v) => { password = v; }, onToggle: () => { shown = !shown; update(); } })}
          <button class="btn" type="submit" ?disabled=${busy}>${say.account.createAccount}</button>
        </form>
        <p class="foot">${say.account.haveAccount} <a href="/sign-in">${say.account.signIn}</a></p>
        <p class="foot"><a href="/help" data-help-link>${say.help.link}</a></p>
      </div>`;
  };
  const update = () => render(view(), root);

  async function onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    if (busy) return;
    busy = true; error = null; update();
    const result = await api.join(token, { gomId, password, displayName });
    // A new account sees the welcome page once, with the group the invite put them in.
    if (result.ok) { go(result.data.group ? `/welcome?group=${encodeURIComponent(result.data.group)}` : '/welcome'); return; }
    busy = false;
    if (result.error === 'invalid_link') state = { kind: 'dead' }; else error = result;
    update();
  }

  update();
  void api.peekInvite(token).then((result) => {
    state = result.ok ? { kind: 'form', invitedBy: result.data.invitedBy } : { kind: 'dead' };
    update();
  });
}
