import { html, nothing, render } from 'lit-html';
import type { Api, Failure, GroupMember, Person } from '../api.js';
import { initials } from '../chat/model.js';
import { appBar, errorBox } from '../ui.js';

/** Who is in a group, asking another member to be friends, adding more of your friends, and leaving. */
export function mountGroup(root: HTMLElement, api: Api, go: (path: string) => void, conversation: string): void {
  let members: GroupMember[] | null = null;
  let friends: Person[] = [];
  let confirming = false;
  let error: Failure | null = null;

  const action = (m: GroupMember) => {
    if (m.isMe || m.isFriend) return nothing;
    if (m.request === 'sent') return html`<span class="tag">Đã gửi lời mời</span>`;
    return html`<button class="btn secondary small" type="button" data-befriend @click=${() => run(() => api.askMember(conversation, m.id))}>${m.request === 'received' ? 'Chấp nhận kết bạn' : 'Kết bạn'}</button>`;
  };

  const memberRow = (m: GroupMember) => html`
    <li class="li" data-member=${m.id}>
      <div class="av sm a1" aria-hidden="true">${initials(m.displayName)}</div>
      <div class="t"><b>${m.displayName}${m.isMe ? html`<span class="tag">Bạn</span>` : nothing}</b>${m.gomId ? html`<small>@${m.gomId}</small>` : nothing}</div>
      ${action(m)}
    </li>`;

  const view = () => html`
    ${appBar({ title: 'Thành viên nhóm', back: `/c/${conversation}` })}
    <div class="page">
      ${errorBox(error)}
      ${members === null ? nothing : html`
        <h2 class="section">${members.length} thành viên</h2>
        <ul class="list">${members.map(memberRow)}</ul>

        ${addable().length === 0 ? nothing : html`
          <h2 class="section">Thêm thành viên</h2>
          <ul class="list">${addable().map((f) => html`
            <li class="li" data-addable=${f.gomId}>
              <div class="t"><b>${f.displayName}</b><small>@${f.gomId}</small></div>
              <button class="btn secondary small" type="button" @click=${() => run(() => api.addMember(conversation, f.gomId))}>Thêm</button>
            </li>`)}</ul>`}

        ${confirming ? html`
          <div class="card" role="group" aria-labelledby="leave-q">
            <p id="leave-q"><b>Rời nhóm này?</b></p>
            <p class="hint">Bạn sẽ không còn thấy tin nhắn của nhóm. Một thành viên có thể thêm bạn lại, nhưng bạn chỉ thấy tin sau khi được thêm.</p>
            <button class="btn" type="button" data-confirm-leave @click=${leave}>Rời nhóm</button>
            <button class="btn secondary" type="button" data-cancel-leave @click=${() => { confirming = false; update(); }}>Ở lại</button>
          </div>` : html`<button class="btn secondary" type="button" data-leave @click=${() => { confirming = true; update(); }}>Rời nhóm</button>`}`}
    </div>`;
  const update = () => render(view(), root);

  /** Your friends who are not in the group yet. A friend in the group shows with a Gom ID, so the Gom ID is how we tell. */
  function addable(): Person[] {
    const inside = new Set((members ?? []).map((m) => m.gomId).filter((g): g is string => g !== null));
    return friends.filter((f) => !inside.has(f.gomId));
  }

  async function reload(): Promise<void> {
    const [m, f] = await Promise.all([api.groupMembers(conversation), api.friends()]);
    if (m.ok) members = m.data; else { error = m; update(); return; }
    if (f.ok) friends = f.data.friends;
    error = null;
    update();
  }

  async function run(call: () => Promise<{ ok: true } | Failure>): Promise<void> {
    const result = await call();
    if (!result.ok) { error = result; update(); return; }
    await reload();
  }

  async function leave(): Promise<void> {
    const result = await api.leaveGroup(conversation);
    if (!result.ok) { error = result; confirming = false; update(); return; }
    go('/');
  }

  update();
  void api.me().then(async (result) => {
    if (!result.ok) { if (result.error === 'unauthorized') go('/sign-in'); else { error = result; update(); } return; }
    await reload();
  });
}
