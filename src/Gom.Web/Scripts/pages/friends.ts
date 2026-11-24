import { html, nothing, render, type TemplateResult } from 'lit-html';
import type { Api, FriendLists, Failure, Person, User } from '../api.js';
import * as icons from '../icons.js';
import { appBar, errorBox, formatTime } from '../ui.js';

const NONE: FriendLists = { friends: [], incoming: [], outgoing: [] };

/** Friends: add by ID or by your own link, answer requests, message, unfriend, block. */
export function mountFriends(root: HTMLElement, api: Api, go: (path: string) => void, origin: string): void {
  let user: User | null = null;
  let lists: FriendLists = NONE;
  let blocked: Person[] = [];
  let friendId = '';
  let status = '';
  let copied = '';
  let busy = false;
  let error: Failure | null = null;

  const button = (label: string, attr: string, run: () => void, secondary = true): TemplateResult =>
    html`<button class="btn ${secondary ? 'secondary ' : ''}small" type="button" data-action=${attr} @click=${run}>${label}</button>`;
  const who = (p: Person, extra: TemplateResult | typeof nothing = nothing) =>
    html`<div class="t"><b>${p.displayName}</b><small>@${p.gomId}${extra}</small></div>`;

  /** A titled list, or nothing at all when it is empty. */
  const section = (title: string, rows: TemplateResult[]) => rows.length === 0 ? nothing : html`
    <h2 class="section">${title}</h2>
    <ul class="list">${rows}</ul>`;

  const view = () => html`
    ${appBar({ title: 'Bạn bè', back: '/' })}
    <div class="page">
      ${errorBox(error)}
      ${user === null ? nothing : html`
        <form class="card" data-add @submit=${onAdd}>
          <label for="friend-id">Thêm bạn bằng Gom ID</label>
          <div class="input"><span class="prefix" aria-hidden="true">@</span>
            <input id="friend-id" name="friendId" type="text" autocomplete="off" autocapitalize="none" autocorrect="off" spellcheck="false"
              enterkeyhint="send" .value=${friendId} @input=${(e: Event) => { friendId = (e.target as HTMLInputElement).value; }}></div>
          <button class="btn" type="submit" ?disabled=${busy}>Gửi lời mời</button>
          <p class="hint" role="status" data-add-status>${status}</p>
        </form>

        <div class="card">
          <label for="my-link">Liên kết kết bạn của bạn</label>
          <div class="input"><input id="my-link" data-mylink readonly type="text" .value=${origin + '/add/' + user.gomId}>
            <button class="ib" type="button" data-copy aria-label="Sao chép liên kết" @click=${copyLink}>${icons.copy()}</button></div>
          <p class="hint">Ai có liên kết này và đã đăng nhập có thể gửi lời mời kết bạn cho bạn.</p>
          <p class="hint" role="status" data-copy-status>${copied}</p>
        </div>

        ${section('Lời mời nhận được', lists.incoming.map((p) => html`
          <li class="li" data-incoming=${p.gomId}>${who(p, html` · ${formatTime(p.createdAt)}`)}
            ${button('Chấp nhận', 'accept', () => act(() => api.acceptFriend(p.gomId)), false)}
            ${button('Từ chối', 'decline', () => act(() => api.withdrawFriend(p.gomId)))}</li>`))}

        ${section('Lời mời đã gửi', lists.outgoing.map((p) => html`
          <li class="li" data-outgoing=${p.gomId}>${who(p, html` · ${formatTime(p.createdAt)}`)}
            ${button('Hủy lời mời', 'cancel', () => act(() => api.withdrawFriend(p.gomId)))}</li>`))}

        <h2 class="section">Bạn bè</h2>
        ${lists.friends.length === 0 ? html`<p class="hint">Chưa có bạn bè nào. Hãy gửi lời mời ở trên.</p>` : html`
        <ul class="list">${lists.friends.map((p) => html`
          <li class="li" data-friend=${p.gomId}>${who(p)}
            ${button('Nhắn tin', 'message', () => void message(p.gomId), false)}
            ${button('Xóa bạn', 'unfriend', () => act(() => api.unfriend(p.gomId)))}
            ${button('Chặn', 'block', () => act(() => api.block(p.gomId)))}</li>`)}</ul>`}

        ${section('Đã chặn', blocked.map((p) => html`
          <li class="li" data-blocked=${p.gomId}>${who(p)}
            ${button('Bỏ chặn', 'unblock', () => act(() => api.unblock(p.gomId)))}</li>`))}`}
    </div>`;
  const update = () => render(view(), root);

  async function reload(): Promise<void> {
    const [f, b] = await Promise.all([api.friends(), api.blocks()]);
    if (f.ok) lists = f.data; else error = f;
    if (b.ok) blocked = b.data; else error = b;
    update();
  }

  async function act(run: () => Promise<{ ok: true } | Failure>): Promise<void> {
    const result = await run();
    if (!result.ok) { error = result; update(); return; }
    error = null; status = '';
    await reload();
  }

  async function message(gomId: string): Promise<void> {
    const result = await api.openDirect(gomId);
    if (!result.ok) { error = result; update(); return; }
    go('/');
  }

  async function onAdd(event: Event): Promise<void> {
    event.preventDefault();
    const id = friendId.trim();
    if (busy || id === '') return;
    busy = true; error = null; status = ''; update();
    const result = await api.askFriend(id);
    busy = false;
    if (!result.ok) { error = result; update(); return; }
    status = result.data.status === 'friends' ? `Bạn và @${id.toLowerCase()} đã là bạn bè.` : `Đã gửi lời mời đến @${id.toLowerCase()}.`;
    friendId = '';
    await reload();
  }

  async function copyLink(): Promise<void> {
    if (user === null) return;
    try { await navigator.clipboard.writeText(origin + '/add/' + user.gomId); copied = 'Đã sao chép'; } catch { copied = 'Không sao chép được. Hãy chọn và chép liên kết bằng tay.'; }
    update();
  }

  update();
  void api.me().then(async (result) => {
    if (!result.ok) {
      if (result.error === 'unauthorized') go('/sign-in'); else { error = result; update(); }
      return;
    }
    user = result.data;
    await reload();
  });
}
