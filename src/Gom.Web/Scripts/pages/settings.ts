import { html, nothing, render } from 'lit-html';
import type { Api, Failure, SessionInfo, User } from '../api.js';
import { appBar, errorBox, formatTime } from '../ui.js';

export function mountSettings(root: HTMLElement, api: Api, go: (path: string) => void): void {
  let user: User | null = null;
  let sessions: SessionInfo[] = [];
  let error: Failure | null = null;

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
    await load();
  });
}
