import { html, render } from 'lit-html';
import type { Api, Failure } from '../api.js';
import { brand, deadLink, errorBox, idField, passwordField } from '../ui.js';
import * as icons from '../icons.js';

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
    if (state.kind === 'dead') return deadLink('Lời mời không dùng được nữa', 'Lời mời này có thể đã hết hạn hoặc đã được dùng. Hãy nhờ người mời gửi cho bạn lời mời mới.');
    if (state.kind === 'loading') return html`<div class="page">${brand()}</div>`;
    return html`
      <div class="page">
        ${brand()}
        <div><h1>Bạn được mời vào Gom</h1><p class="lead">Gom là nơi trò chuyện riêng của gia đình và bạn bè.</p></div>
        <div class="note">${icons.logo()}<div><b>${state.invitedBy}</b> mời bạn vào Gom.</div></div>
        <form @submit=${onSubmit}>
          ${errorBox(error)}
          <div class="field">
            <label for="display-name">Tên hiển thị</label>
            <div class="input"><input id="display-name" name="displayName" type="text" autocomplete="nickname" enterkeyhint="next"
              .value=${displayName} @input=${(e: Event) => { displayName = (e.target as HTMLInputElement).value; }}></div>
            <p class="hint">Có thể để trống: Gom sẽ dùng Gom ID của bạn.</p>
          </div>
          ${idField({ value: gomId, enterkeyhint: 'next', onInput: (v) => { gomId = v; },
            hint: 'Chữ thường, số và dấu gạch dưới, 3 đến 20 ký tự.' })}
          ${passwordField({ label: 'Mật khẩu', autocomplete: 'new-password', value: password, shown, hint: 'Ít nhất 10 ký tự.',
            onInput: (v) => { password = v; }, onToggle: () => { shown = !shown; update(); } })}
          <button class="btn" type="submit" ?disabled=${busy}>Tạo tài khoản</button>
        </form>
        <p class="foot">Đã có tài khoản? <a href="/sign-in">Đăng nhập</a></p>
      </div>`;
  };
  const update = () => render(view(), root);

  async function onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    if (busy) return;
    busy = true; error = null; update();
    const result = await api.join(token, { gomId, password, displayName });
    if (result.ok) { go('/'); return; }
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
