import { html, nothing, render } from 'lit-html';
import { live as liveValue } from 'lit-html/directives/live.js';
import type { Api, BanRow, Failure, GroupMember, JoinRequestRow, Person } from '../api.js';
import { initials } from '../chat/model.js';
import { appBar, errorBox } from '../ui.js';

/** Who is in a group and who leads it, asking another member to be friends, adding friends, leaving, and what a leader or deputy may do. */
export function mountGroup(root: HTMLElement, api: Api, go: (path: string) => void, conversation: string): void {
  let members: GroupMember[] | null = null;
  let title = '';
  let draftTitle = '';
  let renaming = false;
  let joinApproval = false;
  let requests: JoinRequestRow[] = [];
  let bans: BanRow[] = [];
  /** "Do not let them back" ticked in the question about removing someone. */
  let banThem = false;
  let addStatus = '';
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
  const mayManage = () => mine()?.role === 'leader' || mine()?.role === 'deputy';

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
      <button class="btn secondary" type="button" data-remove @click=${() => { sure = { kind: 'remove', member: m }; banThem = false; update(); }}>Xóa khỏi nhóm</button>
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
      ${s.kind === 'remove' ? html`<label class="check"><input type="checkbox" data-ban .checked=${liveValue(banThem)} @change=${(e: Event) => { banThem = (e.target as HTMLInputElement).checked; }}> Không cho vào lại nhóm</label>` : nothing}
      <button class="btn" type="button" data-confirm-sure @click=${proceed}>${text.yes}</button>
      <button class="btn secondary" type="button" data-cancel-sure @click=${() => { sure = null; update(); }}>Hủy</button>
    </div>`;
  };

  async function proceed(): Promise<void> {
    const s = sure;
    const ban = banThem;
    sure = null; menuFor = null; banThem = false;
    if (!s) return;
    if (s.kind === 'disband') {
      const result = await api.disband(conversation);
      if (!result.ok) { error = result; update(); return; }
      go('/');
      return;
    }
    const member = s.member;
    if (!member) return;
    await run(() => s.kind === 'remove' ? api.removeMember(conversation, member.id, ban) : api.handOver(conversation, member.id));
  }

  /** The leader and the deputies may rename; everyone else just sees the name. */
  const mayRename = () => mine()?.role === 'leader' || mine()?.role === 'deputy';

  const nameSection = () => !mayRename()
    ? html`<h2 class="section" data-group-name>${title}</h2>`
    : html`
      <form class="card" data-rename @submit=${onRename}>
        <label for="group-name">Tên nhóm</label>
        <input id="group-name" name="title" type="text" maxlength="60" autocomplete="off" .value=${liveValue(draftTitle)}
          @input=${(e: Event) => { draftTitle = (e.target as HTMLInputElement).value; update(); }}>
        <button class="btn" type="submit" ?disabled=${renaming || draftTitle.trim() === '' || draftTitle.trim() === title}>Lưu tên</button>
      </form>`;

  const view = () => html`
    ${appBar({ title: 'Thành viên nhóm', back: `/c/${conversation}` })}
    <div class="page">
      ${errorBox(error)}
      ${members === null ? nothing : html`
        ${nameSection()}
        <h2 class="section">${members.length} thành viên</h2>
        <ul class="list">${members.map(memberRow)}</ul>

        ${addable().length === 0 ? nothing : html`
          <h2 class="section" id="add">Thêm thành viên</h2>
          <ul class="list">${addable().map((f) => html`
            <li class="li" data-addable=${f.gomId}>
              <div class="t"><b>${f.displayName}</b><small>@${f.gomId}</small></div>
              <button class="btn secondary small" type="button" @click=${() => void add(f.gomId)}>Thêm</button>
            </li>`)}</ul>`}

        ${addStatus === '' ? nothing : html`<p class="hint" role="status" data-add-status>${addStatus}</p>`}

        ${leads() ? html`
          <label class="check card" data-approval-switch><input type="checkbox" data-join-approval .checked=${liveValue(joinApproval)}
            @change=${(e: Event) => { void run(() => api.setJoinApproval(conversation, (e.target as HTMLInputElement).checked)); }}>
            Thành viên thêm người cần được trưởng nhóm hoặc phó nhóm duyệt</label>` : nothing}

        ${mayManage() && requests.length > 0 ? html`
          <h2 class="section">Chờ duyệt</h2>
          <ul class="list" data-requests>${requests.map((r) => html`
            <li class="li" data-request=${r.id}>
              <div class="t"><b>${r.displayName}</b><small>${r.by.displayName} muốn thêm</small></div>
              <button class="btn small" type="button" data-approve @click=${() => run(() => api.approveRequest(conversation, r.id))}>Duyệt</button>
              <button class="btn secondary small" type="button" data-decline @click=${() => run(() => api.declineRequest(conversation, r.id))}>Từ chối</button>
            </li>`)}</ul>` : nothing}

        ${mayManage() && bans.length > 0 ? html`
          <h2 class="section">Không được vào lại</h2>
          <ul class="list" data-bans>${bans.map((b) => html`
            <li class="li" data-ban-row=${b.id}>
              <div class="t"><b>${b.displayName}</b></div>
              ${leads() ? html`<button class="btn secondary small" type="button" data-unban @click=${() => run(() => api.unban(conversation, b.id))}>Bỏ chặn</button>` : nothing}
            </li>`)}</ul>` : nothing}

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

  async function onRename(event: Event): Promise<void> {
    event.preventDefault();
    const wanted = draftTitle.trim();
    if (renaming || wanted === '' || wanted === title) return;
    renaming = true; update();
    const result = await api.renameGroup(conversation, wanted);
    renaming = false;
    if (!result.ok) { error = result; update(); return; }
    await reload();
  }

  /** Adds a friend, or (in a group that asks for approval) asks for it to be approved. */
  async function add(gomId: string): Promise<void> {
    const result = await api.addMember(conversation, gomId);
    if (!result.ok) { error = result; update(); return; }
    addStatus = result.data?.status === 'pending' ? 'Đã gửi yêu cầu, chờ trưởng nhóm hoặc phó nhóm duyệt.' : '';
    await reload();
  }

  async function reload(): Promise<void> {
    const [m, f, c] = await Promise.all([api.groupMembers(conversation), api.friends(), api.conversations()]);
    if (m.ok) members = m.data; else { error = m; update(); return; }
    if (f.ok) friends = f.data.friends;
    if (c.ok) { const row = c.data.find((x) => x.id === conversation); title = row?.title ?? ''; draftTitle = title; joinApproval = row?.joinApproval === true; }
    // The leader and the deputies also see what waits for an answer and who may not come back.
    requests = []; bans = [];
    if (mayManage()) {
      const [rq, bn] = await Promise.all([api.joinRequests(conversation), api.bans(conversation)]);
      if (rq.ok) requests = rq.data.requests;
      if (bn.ok) bans = bn.data.bans;
    }
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
