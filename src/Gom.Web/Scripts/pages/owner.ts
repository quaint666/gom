import { html, nothing, render } from 'lit-html';
import type { Api, Failure, Invite, Issued, Member } from '../api.js';
import * as icons from '../icons.js';
import { matches } from '../search.js';
import { appBar, errorBox, formatTime } from '../ui.js';

/** How many rows of a long list show before "Xem thêm". */
const HANDFUL = 5;

const STATUS: Record<Invite['status'], string> = { pending: 'Đang chờ', used: 'Đã dùng', revoked: 'Đã thu hồi', expired: 'Hết hạn' };

/** The owner's page: make invites, see who joined, make a reset link for a member. */
export function mountOwner(root: HTMLElement, api: Api, go: (path: string) => void, origin: string): void {
  let invites: Invite[] = [];
  let members: Member[] = [];
  let link: { url: string; note: string } | null = null;
  let copied = '';
  let error: Failure | null = null;
  let query = '';
  let moreInvites = false;
  let moreMembers = false;

  const inviteRow = (i: Invite) => html`
    <li class="li" data-invite=${i.id}>
      <div class="t">
        <b>${i.status === 'used' && i.usedBy ? `Đã dùng bởi @${i.usedBy}` : STATUS[i.status]}</b>
        <small>Tạo ${formatTime(i.createdAt)}</small>
      </div>
      ${i.status === 'pending' ? html`<button class="btn secondary small" type="button" @click=${() => revoke(i.id)}>Thu hồi</button>` : nothing}
    </li>`;

  const memberRow = (m: Member) => html`
    <li class="li" data-member=${m.gomId}>
      <div class="t"><b>${m.displayName}${m.isOwner ? html`<span class="tag">Chủ</span>` : nothing}${m.disabled ? html`<span class="tag">Đã khóa</span>` : nothing}</b><small>@${m.gomId}</small></div>
      ${m.isOwner || m.disabled ? nothing : html`<button class="btn secondary small" type="button" @click=${() => makeReset(m.gomId)}>Tạo liên kết đặt lại mật khẩu</button>`}
      ${m.isOwner ? nothing : m.disabled
        ? html`<button class="btn secondary small" type="button" data-enable @click=${() => setDisabled(m.gomId, false)}>Mở khóa</button>`
        : html`<button class="btn secondary small" type="button" data-disable @click=${() => setDisabled(m.gomId, true)}>Khóa tài khoản</button>`}
    </li>`;

  const linkBox = () => link === null ? nothing : html`
    <div class="card">
      <label for="link">Liên kết (chỉ hiện một lần)</label>
      <div class="input"><input id="link" data-link readonly type="text" .value=${link.url}>
        <button class="ib" type="button" data-copy aria-label="Sao chép liên kết" @click=${copyLink}>${icons.copy()}</button></div>
      <p class="hint">${link.note}</p>
      <p class="hint" role="status">${copied}</p>
    </div>`;

  /** A handful of members, or everyone who matches what was typed. */
  const shownMembers = (): Member[] => {
    if (query.trim() !== '') return members.filter((m) => matches(`${m.displayName} ${m.gomId}`, query));
    return moreMembers || members.length <= HANDFUL ? members : members.slice(0, HANDFUL);
  };

  const view = () => html`
    ${appBar({ title: 'Lời mời và thành viên', back: '/settings' })}
    <div class="page">
      ${errorBox(error)}
      <button class="btn" type="button" data-create-invite @click=${makeInvite}>Tạo lời mời mới</button>
      ${linkBox()}
      <h2 class="section">Lời mời</h2>
      <ul class="list">${(moreInvites || invites.length <= HANDFUL ? invites : invites.slice(0, HANDFUL)).map(inviteRow)}</ul>
      ${moreInvites || invites.length <= HANDFUL ? nothing : html`<button class="btn secondary" type="button" data-more-invites @click=${() => { moreInvites = true; update(); }}>Xem thêm (${invites.length - HANDFUL})</button>`}
      <h2 class="section">Thành viên</h2>
      ${members.length <= HANDFUL ? nothing : html`
        <div class="searchfield input">${icons.search()}
          <input type="search" name="member-search" aria-label="Tìm thành viên" placeholder="Tìm thành viên" autocomplete="off" maxlength="60"
            .value=${query} @input=${(e: Event) => { query = (e.target as HTMLInputElement).value; update(); }}></div>`}
      ${shownMembers().length === 0 ? html`<p class="hint" data-no-member>Không tìm thấy thành viên nào.</p>` : html`<ul class="list">${shownMembers().map(memberRow)}</ul>`}
      ${query.trim() !== '' || moreMembers || members.length <= HANDFUL ? nothing : html`<button class="btn secondary" type="button" data-more-members @click=${() => { moreMembers = true; update(); }}>Xem thêm (${members.length - HANDFUL})</button>`}
    </div>`;
  const update = () => render(view(), root);

  const show = (issued: Issued, note: string) => { link = { url: origin + issued.path, note }; copied = ''; error = null; };

  async function reload(): Promise<void> {
    const [i, m] = await Promise.all([api.invites(), api.members()]);
    if (i.ok) invites = i.data; else error = i;
    if (m.ok) members = m.data; else error = m;
    update();
  }

  async function makeInvite(): Promise<void> {
    const result = await api.createInvite();
    if (!result.ok) { error = result; update(); return; }
    show(result.data, 'Lời mời có hiệu lực 7 ngày và chỉ dùng được một lần.');
    await reload();
  }

  async function makeReset(gomId: string): Promise<void> {
    const result = await api.resetLink(gomId);
    if (!result.ok) { error = result; update(); return; }
    show(result.data, 'Liên kết có hiệu lực 24 giờ và chỉ dùng được một lần. Hãy gửi cho đúng người đó.');
    update();
  }

  async function setDisabled(gomId: string, disabled: boolean): Promise<void> {
    const result = await (disabled ? api.disableMember(gomId) : api.enableMember(gomId));
    if (!result.ok) { error = result; update(); return; }
    error = null;
    await reload();
  }

  async function revoke(id: string): Promise<void> {
    const result = await api.revokeInvite(id);
    if (!result.ok) { error = result; update(); return; }
    await reload();
  }

  async function copyLink(): Promise<void> {
    if (link === null) return;
    try { await navigator.clipboard.writeText(link.url); copied = 'Đã sao chép'; } catch { copied = 'Không sao chép được. Hãy chọn và chép liên kết bằng tay.'; }
    update();
  }

  update();
  void api.me().then(async (result) => {
    if (!result.ok) {
      if (result.error === 'unauthorized') go('/sign-in'); else { error = result; update(); }
      return;
    }
    if (!result.data.isOwner) { go('/'); return; }
    await reload();
  });
}
