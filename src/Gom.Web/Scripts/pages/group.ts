import { html, nothing, render } from 'lit-html';
import type { Api, Failure, GroupMember, Person } from '../api.js';
import { initials } from '../chat/model.js';
import { appBar, errorBox } from '../ui.js';

/** Who is in a group and who leads it, asking another member to be friends, adding friends, leaving, and what a leader or deputy may do. */
export function mountGroup(root: HTMLElement, api: Api, go: (path: string) => void, conversation: string): void {
  let members: GroupMember[] | null = null;
  let friends: Person[] = [];
  let confirming = false;
  /** Whose menu is open, and the step that needs a yes first. */
  let menuFor: string | null = null;
  let sure: { kind: 'remove' | 'handover' | 'disband'; member?: GroupMember } | null = null;
  let error: Failure | null = null;

  const action = (m: GroupMember) => {
    if (m.isMe || m.isFriend) return nothing;
    if (m.request === 'sent') return html`<span class="tag">Đã gửi lời mời</span>`;
    return html`<button class="btn secondary small" type="button" data-befriend @click=${() => run(() => api.askMember(conversation, m.id))}>${m.request === 'received' ? 'Chấp nhận kết bạn' : 'Kết bạn'}</button>`;
  };

  const mine = (): GroupMember | undefined => members?.find((m) => m.isMe);
  const leads = () => mine()?.role === 'leader';

  /** A leader acts on anyone else; a deputy only on plain members. */
  const manages = (m: GroupMember) => {
    const me = mine();
    return !!me && !m.isMe && (me.role === 'leader' || (me.role === 'deputy' && m.role === 'member'));
  };

  const roleTag = (m: GroupMember) => m.role === 'leader' ? html`<span class="tag" data-role="leader">Trưởng nhóm</span>`
    : m.role === 'deputy' ? html`<span class="tag" data-role="deputy">Phó nhóm</span>` : nothing;

  const menu = (m: GroupMember) => html`
    <div class="card" role="group" aria-label=${`Quản lý ${m.displayName}`} data-menu=${m.id}>
      ${leads() && m.role === 'member' ? html`<button class="btn secondary" type="button" data-make-deputy @click=${() => run(() => api.setRole(conversation, m.id, 'deputy'))}>Đặt làm phó nhóm</button>` : nothing}
      ${leads() && m.role === 'deputy' ? html`<button class="btn secondary" type="button" data-drop-deputy @click=${() => run(() => api.setRole(conversation, m.id, 'member'))}>Bỏ phó nhóm</button>` : nothing}
      ${leads() ? html`<button class="btn secondary" type="button" data-hand-over @click=${() => { sure = { kind: 'handover', member: m }; update(); }}>Chuyển quyền trưởng nhóm</button>` : nothing}
      <button class="btn secondary" type="button" data-remove @click=${() => { sure = { kind: 'remove', member: m }; update(); }}>Xóa khỏi nhóm</button>
    </div>`;

  const memberRow = (m: GroupMember) => html`
    <li class="li" data-member=${m.id}>
      <div class="av sm a1" aria-hidden="true">${initials(m.displayName)}</div>
      <div class="t"><b>${m.displayName}${m.isMe ? html`<span class="tag">Bạn</span>` : nothing}${roleTag(m)}</b>${m.gomId ? html`<small>@${m.gomId}</small>` : nothing}</div>
      ${action(m)}
      ${manages(m) ? html`<button class="btn secondary small" type="button" data-manage aria-label=${`Quản lý ${m.displayName}`} aria-expanded=${menuFor === m.id ? 'true' : 'false'}
        @click=${() => { menuFor = menuFor === m.id ? null : m.id; update(); }}>⋮</button>` : nothing}
    </li>
    ${menuFor === m.id && manages(m) ? menu(m) : nothing}`;

  const question = (s: NonNullable<typeof sure>) => {
    const who = s.member?.displayName ?? '';
    const text = s.kind === 'remove' ? { q: `Xóa ${who} khỏi nhóm?`, hint: `${who} sẽ không còn thấy nhóm này. Một thành viên có thể thêm lại, nhưng chỉ thấy tin sau khi được thêm.`, yes: 'Xóa khỏi nhóm' }
      : s.kind === 'handover' ? { q: `Chuyển quyền trưởng nhóm cho ${who}?`, hint: 'Bạn sẽ trở thành thành viên thường và không thể lấy lại quyền nếu người kia không chuyển lại.', yes: 'Chuyển quyền' }
      : { q: 'Giải tán nhóm này?', hint: 'Nhóm và toàn bộ tin nhắn sẽ bị xóa với mọi người. Không thể khôi phục.', yes: 'Giải tán nhóm' };
    return html`<div class="card" role="group" aria-labelledby="sure-q">
      <p id="sure-q"><b>${text.q}</b></p><p class="hint">${text.hint}</p>
      <button class="btn" type="button" data-confirm-sure @click=${proceed}>${text.yes}</button>
      <button class="btn secondary" type="button" data-cancel-sure @click=${() => { sure = null; update(); }}>Hủy</button>
    </div>`;
  };

  async function proceed(): Promise<void> {
    const s = sure;
    sure = null; menuFor = null;
    if (!s) return;
    if (s.kind === 'disband') {
      const result = await api.disband(conversation);
      if (!result.ok) { error = result; update(); return; }
      go('/');
      return;
    }
    const member = s.member;
    if (!member) return;
    await run(() => s.kind === 'remove' ? api.removeMember(conversation, member.id) : api.handOver(conversation, member.id));
  }

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

        ${sure ? question(sure) : nothing}

        ${leads() && members.length > 1 ? html`
          <button class="btn secondary" type="button" disabled data-leave>Rời nhóm</button>
          <p class="hint" data-leader-note>Chuyển quyền trưởng nhóm cho người khác trước khi rời nhóm.</p>` : confirming ? html`
          <div class="card" role="group" aria-labelledby="leave-q">
            <p id="leave-q"><b>Rời nhóm này?</b></p>
            <p class="hint">Bạn sẽ không còn thấy tin nhắn của nhóm. Một thành viên có thể thêm bạn lại, nhưng bạn chỉ thấy tin sau khi được thêm.</p>
            <button class="btn" type="button" data-confirm-leave @click=${leave}>Rời nhóm</button>
            <button class="btn secondary" type="button" data-cancel-leave @click=${() => { confirming = false; update(); }}>Ở lại</button>
          </div>` : html`<button class="btn secondary" type="button" data-leave @click=${() => { confirming = true; update(); }}>Rời nhóm</button>`}
        ${leads() ? html`<button class="btn secondary" type="button" data-disband @click=${() => { sure = { kind: 'disband' }; update(); }}>Giải tán nhóm</button>` : nothing}`}
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
