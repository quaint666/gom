import { html, nothing, render } from 'lit-html';
import { live as liveValue } from 'lit-html/directives/live.js';
import type { Api, BanRow, Failure, GroupMember, JoinRequestRow, Person } from '../api.js';
import { appBar, avatar, errorBox } from '../ui.js';
import { limits } from '../limits.js';
import { say } from '../say.js';

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
    if (m.request === 'sent') return html`<span class="tag">${say.group.requestSent}</span>`;
    return html`<button class="btn secondary small" type="button" data-befriend @click=${() => run(() => api.askMember(conversation, m.id))}>${m.request === 'received' ? say.common.acceptFriend : say.common.befriend}</button>`;
  };

  const mine = (): GroupMember | undefined => members?.find((m) => m.isMe);
  const leads = () => mine()?.role === 'leader';
  const mayManage = () => mine()?.role === 'leader' || mine()?.role === 'deputy';

  /** A leader acts on anyone else; a deputy only on plain members. */
  const manages = (m: GroupMember) => {
    const me = mine();
    return !!me && !m.isMe && (me.role === 'leader' || (me.role === 'deputy' && m.role === 'member'));
  };

  const roleTag = (m: GroupMember) => m.role === 'leader' ? html`<span class="tag" data-role="leader">${say.group.leaderTag}</span>`
    : m.role === 'deputy' ? html`<span class="tag" data-role="deputy">${say.common.deputy}</span>` : nothing;

  const menu = (m: GroupMember) => html`
    <div class="card" role="group" aria-label=${say.group.manage(m.displayName)} data-menu=${m.id}>
      ${leads() && m.role === 'member' ? html`<button class="btn secondary" type="button" data-make-deputy @click=${() => run(() => api.setRole(conversation, m.id, 'deputy'))}>${say.group.makeDeputy}</button>` : nothing}
      ${leads() && m.role === 'deputy' ? html`<button class="btn secondary" type="button" data-drop-deputy @click=${() => run(() => api.setRole(conversation, m.id, 'member'))}>${say.group.dropDeputy}</button>` : nothing}
      ${leads() ? html`<button class="btn secondary" type="button" data-hand-over @click=${() => { sure = { kind: 'handover', member: m }; update(); }}>${say.group.handOver}</button>` : nothing}
      <button class="btn secondary" type="button" data-remove @click=${() => { sure = { kind: 'remove', member: m }; banThem = false; update(); }}>${say.group.remove}</button>
    </div>`;

  const memberRow = (m: GroupMember) => html`
    <li class="li" data-member=${m.id}>
      ${avatar(m.displayName, true, false, m.avatar)}
      <div class="t"><b>${m.displayName}${m.isMe ? html`<span class="tag">${say.common.you}</span>` : nothing}${roleTag(m)}</b>${m.gomId ? html`<small>@${m.gomId}</small>` : nothing}</div>
      ${action(m)}
      ${manages(m) ? html`<button class="btn secondary small" type="button" data-manage aria-label=${say.group.manage(m.displayName)} aria-expanded=${menuFor === m.id ? 'true' : 'false'}
        @click=${() => { menuFor = menuFor === m.id ? null : m.id; update(); }}>⋮</button>` : nothing}
    </li>
    ${menuFor === m.id && manages(m) ? menu(m) : nothing}`;

  const question = (s: NonNullable<typeof sure>) => {
    const who = s.member?.displayName ?? '';
    const text = s.kind === 'remove' ? { q: say.group.removeQ(who), hint: say.group.removeNote(who), yes: say.group.remove }
      : s.kind === 'handover' ? { q: say.group.handOverQ(who), hint: say.group.handOverNote, yes: say.group.handOverYes }
      : { q: say.group.disbandQ, hint: say.group.disbandNote, yes: say.group.disband };
    return html`<div class="card" role="group" aria-labelledby="sure-q">
      <p id="sure-q"><b>${text.q}</b></p><p class="hint">${text.hint}</p>
      ${s.kind === 'remove' ? html`<label class="check"><input type="checkbox" data-ban .checked=${liveValue(banThem)} @change=${(e: Event) => { banThem = (e.target as HTMLInputElement).checked; }}> ${say.group.ban}</label>` : nothing}
      <button class="btn" type="button" data-confirm-sure @click=${proceed}>${text.yes}</button>
      <button class="btn secondary" type="button" data-cancel-sure @click=${() => { sure = null; update(); }}>${say.common.cancel}</button>
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
        <label for="group-name">${say.group.name}</label>
        <div class="input"><input id="group-name" name="title" type="text" maxlength=${limits().nameChars} autocomplete="off" .value=${liveValue(draftTitle)}
          @input=${(e: Event) => { draftTitle = (e.target as HTMLInputElement).value; update(); }}></div>
        <button class="btn" type="submit" ?disabled=${renaming || draftTitle.trim() === '' || draftTitle.trim() === title}>${say.settings.saveName}</button>
      </form>`;

  const view = () => html`
    ${appBar({ title: say.group.title, back: `/c/${conversation}` })}
    <div class="page">
      ${errorBox(error)}
      ${members === null ? nothing : html`
        ${nameSection()}
        <h2 class="section">${say.common.members(members.length)}</h2>
        <ul class="list">${members.map(memberRow)}</ul>

        ${addable().length === 0 ? nothing : html`
          <h2 class="section" id="add">${say.group.addMembers}</h2>
          <ul class="list">${addable().map((f) => html`
            <li class="li" data-addable=${f.gomId}>
              <div class="t"><b>${f.displayName}</b><small>@${f.gomId}</small></div>
              <button class="btn secondary small" type="button" @click=${() => void add(f.gomId)}>${say.group.add}</button>
            </li>`)}</ul>`}

        ${addStatus === '' ? nothing : html`<p class="hint" role="status" data-add-status>${addStatus}</p>`}

        ${leads() ? html`
          <label class="check card" data-approval-switch><input type="checkbox" data-join-approval .checked=${liveValue(joinApproval)}
            @change=${(e: Event) => { void run(() => api.setJoinApproval(conversation, (e.target as HTMLInputElement).checked)); }}>
            ${say.group.approvalSwitch}</label>` : nothing}

        ${mayManage() && requests.length > 0 ? html`
          <h2 class="section">${say.group.waiting}</h2>
          <ul class="list" data-requests>${requests.map((r) => html`
            <li class="li" data-request=${r.id}>
              <div class="t"><b>${r.displayName}</b><small>${say.group.wantsToAdd(r.by.displayName)}</small></div>
              <button class="btn small" type="button" data-approve @click=${() => run(() => api.approveRequest(conversation, r.id))}>${say.group.approve}</button>
              <button class="btn secondary small" type="button" data-decline @click=${() => run(() => api.declineRequest(conversation, r.id))}>${say.group.decline}</button>
            </li>`)}</ul>` : nothing}

        ${mayManage() && bans.length > 0 ? html`
          <h2 class="section">${say.group.banned}</h2>
          <ul class="list" data-bans>${bans.map((b) => html`
            <li class="li" data-ban-row=${b.id}>
              <div class="t"><b>${b.displayName}</b></div>
              ${leads() ? html`<button class="btn secondary small" type="button" data-unban @click=${() => run(() => api.unban(conversation, b.id))}>${say.group.unban}</button>` : nothing}
            </li>`)}</ul>` : nothing}

        ${sure ? question(sure) : nothing}

        ${leads() && members.length > 1 ? html`
          <button class="btn secondary" type="button" disabled data-leave>${say.common.leaveGroup}</button>
          <p class="hint" data-leader-note>${say.chat.leaderMustHandOver}</p>` : confirming ? html`
          <div class="card" role="group" aria-labelledby="leave-q">
            <p id="leave-q"><b>${say.chat.leaveQ}</b></p>
            <p class="hint">${say.group.leaveNote}</p>
            <button class="btn" type="button" data-confirm-leave @click=${leave}>${say.common.leaveGroup}</button>
            <button class="btn secondary" type="button" data-cancel-leave @click=${() => { confirming = false; update(); }}>${say.chat.stay}</button>
          </div>` : html`<button class="btn secondary" type="button" data-leave @click=${() => { confirming = true; update(); }}>${say.common.leaveGroup}</button>`}
        ${leads() ? html`<button class="btn secondary" type="button" data-disband @click=${() => { sure = { kind: 'disband' }; update(); }}>${say.group.disband}</button>` : nothing}`}
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
    addStatus = result.data?.status === 'pending' ? say.group.askedApproval : '';
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
