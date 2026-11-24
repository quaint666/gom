import { html, render } from 'lit-html';
import type { Api, Failure } from '../api.js';
import { brand, deadLink, errorBox, passwordField } from '../ui.js';

type State = { kind: 'loading' } | { kind: 'dead' } | { kind: 'form'; gomId: string };

export function mountReset(root: HTMLElement, api: Api, go: (path: string) => void, token: string): void {
  let state: State = { kind: 'loading' };
  let password = '';
  let shown = false;
  let busy = false;
  let error: Failure | null = null;

  const view = () => {
    if (state.kind === 'dead') return deadLink('Liên kết không dùng được nữa', 'Liên kết này có thể đã hết hạn hoặc đã được dùng. Hãy nhờ người quản lý Gom tạo liên kết mới.');
    if (state.kind === 'loading') return html`<div class="page">${brand()}</div>`;
    return html`
      <div class="page">
        ${brand()}
        <div><h1>Đặt lại mật khẩu</h1><p class="lead">Chọn mật khẩu mới cho <b>@${state.gomId}</b>. Các thiết bị đang đăng nhập sẽ bị đăng xuất.</p></div>
        <form @submit=${onSubmit}>
          ${errorBox(error)}
          <input class="sr-only" name="username" type="text" autocomplete="username" readonly tabindex="-1" aria-hidden="true" .value=${state.gomId}>
          ${passwordField({ label: 'Mật khẩu mới', autocomplete: 'new-password', value: password, shown, hint: 'Ít nhất 10 ký tự.',
            onInput: (v) => { password = v; }, onToggle: () => { shown = !shown; update(); } })}
          <button class="btn" type="submit" ?disabled=${busy}>Lưu mật khẩu</button>
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
