import { html, render } from 'lit-html';
import type { Api, Failure } from '../api.js';
import { brand, errorBox, idField, passwordField } from '../ui.js';

export function mountSignIn(root: HTMLElement, api: Api, go: (path: string) => void): void {
  let gomId = '';
  let password = '';
  let shown = false;
  let busy = false;
  let error: Failure | null = null;

  const view = () => html`
    <div class="page">
      ${brand()}
      <h1 class="center">Đăng nhập</h1>
      <form @submit=${onSubmit}>
        ${errorBox(error)}
        ${idField({ value: gomId, enterkeyhint: 'next', onInput: (v) => { gomId = v; } })}
        ${passwordField({ label: 'Mật khẩu', autocomplete: 'current-password', value: password, shown,
          onInput: (v) => { password = v; }, onToggle: () => { shown = !shown; update(); } })}
        <button class="btn" type="submit" ?disabled=${busy}>Đăng nhập</button>
      </form>
      <p class="foot">Chưa có tài khoản? Gom chỉ dành cho người được mời.</p>
    </div>`;
  const update = () => render(view(), root);

  async function onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    if (busy) return;
    busy = true; error = null; update();
    const result = await api.signIn(gomId, password);
    if (result.ok) { go('/'); return; }
    busy = false; error = result; update();
  }

  update();
}
