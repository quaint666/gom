import { html, nothing, render } from 'lit-html';
import { live } from 'lit-html/directives/live.js';
import type { Api, Failure, SessionInfo, User } from '../api.js';
import { appBar, errorBox, formatTime, passwordField } from '../ui.js';

export function mountSettings(root: HTMLElement, api: Api, go: (path: string) => void): void {
  let user: User | null = null;
  let sessions: SessionInfo[] = [];
  let error: Failure | null = null;
  let nickname = '';
  let nicknameStatus = '';
  let current = '';
  let next = '';
  let shown = false;
  let passwordStatus = '';
  let receipts = true;
  let receiptsStatus = '';
  let busy = false;

  const row = (s: SessionInfo) => html`
    <li class="li" data-session=${s.id}>
      <div class="t">
        <b>${s.deviceLabel}${s.current ? html`<span class="tag">Thiết bị này</span>` : nothing}</b>
        <small>Hoạt động lần cuối: ${formatTime(s.lastSeenAt)}</small>
      </div>
      ${s.current ? nothing : html`<button class="btn secondary small" type="button" @click=${() => endSession(s.id)}>Đăng xuất thiết bị</button>`}
    </li>`;

  const view = () => html`
    ${appBar({ title: 'Cài đặt', back: '/' })}
    <div class="page">
      ${errorBox(error)}
      ${user === null ? nothing : html`
        <p class="lead">Đang đăng nhập là <b>${user.displayName}</b> (@${user.gomId}).</p>
        <h2 class="section">Tên hiển thị</h2>
        <form class="card" data-nickname @submit=${saveNickname}>
          <label for="display-name">Tên mọi người nhìn thấy</label>
          <div class="input"><input id="display-name" name="displayName" type="text" autocomplete="nickname" maxlength="60" enterkeyhint="done"
            .value=${nickname} @input=${(e: Event) => { nickname = (e.target as HTMLInputElement).value; }}></div>
          <p class="hint">Để trống để dùng Gom ID (@${user.gomId}).</p>
          <button class="btn" type="submit" ?disabled=${busy}>Lưu tên</button>
          <p class="hint" role="status" data-nickname-status>${nicknameStatus}</p>
        </form>
        <h2 class="section">Trạng thái đã xem</h2>
        <div class="card">
          <div class="pick"><input type="checkbox" id="receipts" name="receipts" .checked=${live(receipts)} @change=${(e: Event) => void saveReceipts((e.target as HTMLInputElement).checked)}>
            <label for="receipts">Cho người khác biết khi tôi đã xem tin nhắn</label></div>
          <p class="hint">Nếu tắt, bạn cũng không thấy người khác đã xem tin nhắn của bạn.</p>
          <p class="hint" role="status" data-receipts-status>${receiptsStatus}</p>
        </div>
        <h2 class="section">Đổi mật khẩu</h2>
        <form class="card" data-password @submit=${savePassword}>
          <input class="sr-only" name="username" type="text" autocomplete="username" readonly tabindex="-1" aria-hidden="true" .value=${user.gomId}>
          ${passwordField({ label: 'Mật khẩu hiện tại', name: 'current', autocomplete: 'current-password', value: current, shown,
            onInput: (v) => { current = v; }, onToggle: toggleShown })}
          ${passwordField({ label: 'Mật khẩu mới', name: 'password', autocomplete: 'new-password', value: next, shown,
            hint: 'Ít nhất 10 ký tự. Các thiết bị khác sẽ bị đăng xuất.', onInput: (v) => { next = v; }, onToggle: toggleShown })}
          <button class="btn" type="submit" ?disabled=${busy}>Đổi mật khẩu</button>
          <p class="hint" role="status" data-password-status>${passwordStatus}</p>
        </form>
        <h2 class="section">Thiết bị đang đăng nhập</h2>
        <ul class="list">${sessions.map(row)}</ul>
        <button class="btn secondary" type="button" data-signout @click=${signOut}>Đăng xuất</button>
        ${user.isOwner ? html`<a class="btn secondary" href="/owner">Lời mời và thành viên</a>` : nothing}`}
    </div>`;
  const update = () => render(view(), root);

  async function load(): Promise<void> {
    const result = await api.sessions();
    if (result.ok) sessions = result.data; else error = result;
    update();
  }

  async function saveReceipts(enabled: boolean): Promise<void> {
    receipts = enabled; receiptsStatus = ''; error = null;
    const result = await api.setReceipts(enabled);
    if (result.ok) receiptsStatus = 'Đã lưu.'; else { receipts = !enabled; error = result; }
    update();
  }

  const toggleShown = () => { shown = !shown; update(); };

  async function saveNickname(event: Event): Promise<void> {
    event.preventDefault();
    if (busy) return;
    busy = true; error = null; nicknameStatus = ''; update();
    const result = await api.setNickname(nickname);
    busy = false;
    if (result.ok) { user = result.data; nickname = result.data.displayName; nicknameStatus = 'Đã lưu.'; } else error = result;
    update();
  }

  async function savePassword(event: Event): Promise<void> {
    event.preventDefault();
    if (busy) return;
    busy = true; error = null; passwordStatus = ''; update();
    const result = await api.changePassword(current, next);
    busy = false;
    if (!result.ok) { error = result; update(); return; }
    current = ''; next = ''; passwordStatus = 'Đã đổi mật khẩu. Các thiết bị khác đã bị đăng xuất.';
    await load();
  }

  async function endSession(id: string): Promise<void> {
    const result = await api.endSession(id);
    if (!result.ok) { error = result; update(); return; }
    await load();
  }

  async function signOut(): Promise<void> {
    const result = await api.signOut();
    if (result.ok || result.error === 'unauthorized') go('/sign-in'); else { error = result; update(); }
  }

  update();
  void api.me().then(async (result) => {
    if (!result.ok) {
      if (result.error === 'unauthorized') go('/sign-in'); else { error = result; update(); }
      return;
    }
    user = result.data;
    nickname = result.data.displayName;
    receipts = result.data.readReceipts !== false;
    await load();
  });
}
