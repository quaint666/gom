import { html, render } from 'lit-html';
import type { Api, Failure } from '../api.js';
import { brand, deadLink, errorBox, passwordField } from '../ui.js';
import { passwordHint } from '../text.js';
import { say } from '../say.js';

type State = { kind: 'loading' } | { kind: 'dead' } | { kind: 'form'; gomId: string };

export function mountReset(root: HTMLElement, api: Api, go: (path: string) => void, token: string): void {
  let state: State = { kind: 'loading' };
  let password = '';
  let shown = false;
  let busy = false;
  let error: Failure | null = null;

  const view = () => {
    if (state.kind === 'dead') return deadLink(say.account.deadReset, say.account.deadResetNote);
    if (state.kind === 'loading') return html`<div class="page">${brand()}</div>`;
    return html`
      <div class="page">
        ${brand()}
        <div><h1>${say.account.resetTitle}</h1><p class="lead">${say.account.resetFor} <b>@${state.gomId}</b>. ${say.account.resetNote}</p></div>
        <form @submit=${onSubmit}>
          ${errorBox(error)}
          <input class="sr-only" name="username" type="text" autocomplete="username" readonly tabindex="-1" aria-hidden="true" .value=${state.gomId}>
          ${passwordField({ label: say.settings.newPassword, autocomplete: 'new-password', value: password, shown, hint: passwordHint(),
            onInput: (v) => { password = v; }, onToggle: () => { shown = !shown; update(); } })}
          <button class="btn" type="submit" ?disabled=${busy}>${say.account.savePassword}</button>
        </form>
      </div>`;
  };
  const update = () => render(view(), root);

  async function onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    if (busy) return;
    busy = true; error = null; update();
    const result = await api.reset(token, password);
    if (result.ok) { go('/'); return; }
    busy = false;
    if (result.error === 'invalid_link') state = { kind: 'dead' }; else error = result;
    update();
  }

  update();
  void api.peekReset(token).then((result) => {
    state = result.ok ? { kind: 'form', gomId: result.data.gomId } : { kind: 'dead' };
    update();
  });
}
