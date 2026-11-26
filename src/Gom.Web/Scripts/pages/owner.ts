import { html, nothing, render } from 'lit-html';
import type { Api, Failure, Invite, Issued, JoinRequest, Member } from '../api.js';
import * as icons from '../icons.js';
import { matches } from '../search.js';
import { appBar, errorBox, formatTime } from '../ui.js';
import { limits } from '../limits.js';
import { say } from '../say.js';

/** How many rows of a long list show before "Xem thêm". */
const HANDFUL = 5;

const STATUS: Record<Invite['status'], string> = { active: say.owner.active, full: say.owner.full, revoked: say.owner.revoked, expired: say.owner.expired };

/** What the owner may choose for a new link: how long it works, and how many people it lets in (0 is any number). */
const DAYS = [1, 7, 30] as const;
const USES = [1, 10, 0] as const;

/** The owner's page: make invites, see who joined, make a reset link for a member. */
export function mountOwner(root: HTMLElement, api: Api, go: (path: string) => void, origin: string): void {
  let invites: Invite[] = [];
  let members: Member[] = [];
  let link: { url: string; note: string } | null = null;
  let copied = '';
  let error: Failure | null = null;
  let query = '';
  let days: number = limits().inviteDefaultDays;
  let uses: number = limits().inviteDefaultUses;
  let moreInvites = false;
  let moreMembers = false;
  /** Asking to join: whether it is open, and who asked (waiting first, then refused). */
  let signUpOpen = false;
  let requests: JoinRequest[] = [];

  const inviteRow = (i: Invite) => html`
    <li class="li invite" data-invite=${i.id} data-status=${i.status}>
      <div class="t">
        <b>${STATUS[i.status]}</b>
        <small>${say.owner.inviteLine(i.uses, String(i.maxUses ?? '∞'), formatTime(i.createdAt), i.status === 'expired' ? say.owner.hasExpired : say.owner.expires(formatTime(i.expiresAt)))}</small>
        ${i.members.length === 0 ? nothing : html`<small data-joined>${say.owner.joined(i.members.map((m) => `${m.displayName} (@${m.gomId})`).join(', '))}</small>`}
      </div>
      ${i.status === 'active' ? html`<button class="btn secondary small" type="button" data-revoke @click=${() => revoke(i.id)}>${say.owner.revoke}</button>` : nothing}
    </li>`;

  const memberRow = (m: Member) => html`
    <li class="li" data-member=${m.gomId}>
      <div class="t"><b>${m.displayName}${m.isOwner ? html`<span class="tag">${say.owner.ownerTag}</span>` : nothing}${m.deleted ? html`<span class="tag">${say.owner.deletedTag}</span>` : m.disabled ? html`<span class="tag">${say.owner.disabledTag}</span>` : nothing}</b><small>@${m.gomId}</small></div>
      ${m.isOwner || m.deleted ? nothing : html`
        <details class="row-acts" data-member-actions>
          <summary class="ib" aria-label=${say.friends.optionsFor(m.displayName)}>${icons.more()}</summary>
          <div class="row-acts-list">
            ${m.disabled ? nothing : html`<button type="button" data-reset @click=${(e: Event) => { shut(e); makeReset(m.gomId); }}>${say.owner.makeReset}</button>`}
            ${m.disabled
              ? html`<button type="button" data-enable @click=${(e: Event) => { shut(e); void setDisabled(m.gomId, false); }}>${say.owner.enable}</button>`
              : html`<button type="button" class="danger-item" data-disable @click=${(e: Event) => { shut(e); void setDisabled(m.gomId, true); }}>${say.owner.disable}</button>`}
          </div>
        </details>`}
    </li>`;

  /** Closes the row's actions once one is chosen. */
  const shut = (e: Event) => { (e.currentTarget as HTMLElement).closest('details')?.removeAttribute('open'); };

  const linkBox = () => link === null ? nothing : html`
    <div class="card">
      <label for="link">${say.owner.linkOnce}</label>
      <div class="input"><input id="link" data-link readonly type="text" .value=${link.url}>
        <button class="ib" type="button" data-copy aria-label=${say.friends.copyLink} @click=${copyLink}>${icons.copy()}</button></div>
      <p class="hint">${link.note}</p>
      <p class="hint" role="status">${copied}</p>
    </div>`;

  /** A handful of members, or everyone who matches what was typed. */
  const requestRow = (r: JoinRequest) => html`
    <li class="li" data-join-request=${r.gomId}>
      <div class="t"><b>${r.displayName}</b><small>@${r.gomId} · ${say.owner.askedAt(formatTime(r.requestedAt))}</small>
        <span class="request-note">${r.note}</span></div>
      <div class="acts">
        <button class="btn small" type="button" data-approve @click=${() => answer(() => api.approveJoin(r.gomId))}>${say.owner.approve}</button>
        ${r.refused
          ? html`<button class="btn secondary small" type="button" data-delete-request @click=${() => answer(() => api.deleteJoin(r.gomId))}>${say.owner.remove}</button>`
          : html`<button class="btn secondary small" type="button" data-refuse @click=${() => answer(() => api.refuseJoin(r.gomId))}>${say.owner.refuse}</button>`}
      </div>
    </li>`;

  const requestsView = () => {
    const waiting = requests.filter((r) => !r.refused);
    const refused = requests.filter((r) => r.refused);
    return html`
      <h2 class="section">${say.owner.signUp}</h2>
      <div class="pick" data-sign-up-switch><input type="checkbox" id="sign-up-open" data-sign-up-open .checked=${signUpOpen}
        @change=${(e: Event) => void setOpen((e.target as HTMLInputElement).checked)}><label for="sign-up-open">${say.owner.openSignUp}</label></div>
      ${waiting.length === 0 && !signUpOpen ? nothing : html`
        <h3 class="section">${say.owner.waitingCount(waiting.length)}</h3>
        ${waiting.length === 0 ? html`<p class="hint" data-none-waiting>${say.owner.noneWaiting}</p>` : html`<ul class="list" data-waiting-list>${waiting.map(requestRow)}</ul>`}`}
      ${refused.length === 0 ? nothing : html`<h3 class="section">${say.owner.refusedList}</h3><ul class="list" data-refused-list>${refused.map(requestRow)}</ul>`}`;
  };

  const shownMembers = (): Member[] => {
    if (query.trim() !== '') return members.filter((m) => matches(`${m.displayName} ${m.gomId}`, query));
    return moreMembers || members.length <= HANDFUL ? members : members.slice(0, HANDFUL);
  };

  const view = () => html`
    ${appBar({ title: say.owner.title, back: '/settings' })}
    <div class="page">
      ${errorBox(error)}
      <div class="card invite-form">
        <div class="field"><label for="invite-days">${say.owner.validFor}</label>
          <select id="invite-days" name="days" @change=${(e: Event) => { days = Number((e.target as HTMLSelectElement).value); }}>
            ${DAYS.map((d) => html`<option value=${d} ?selected=${d === days}>${say.owner.days(d)}</option>`)}</select></div>
        <div class="field"><label for="invite-uses">${say.owner.letsIn}</label>
          <select id="invite-uses" name="uses" @change=${(e: Event) => { uses = Number((e.target as HTMLSelectElement).value); }}>
            ${USES.map((u) => html`<option value=${u} ?selected=${u === uses}>${u === 0 ? say.owner.anyNumber : say.owner.people(u)}</option>`)}</select></div>
        <button class="btn" type="button" data-create-invite @click=${makeInvite}>${say.owner.makeInvite}</button>
      </div>
      ${linkBox()}
      ${requestsView()}
      <h2 class="section">${say.owner.invites}</h2>
      <ul class="list">${(moreInvites || invites.length <= HANDFUL ? invites : invites.slice(0, HANDFUL)).map(inviteRow)}</ul>
      ${moreInvites || invites.length <= HANDFUL ? nothing : html`<button class="btn secondary" type="button" data-more-invites @click=${() => { moreInvites = true; update(); }}>${say.friends.seeMoreCount(invites.length - HANDFUL)}</button>`}
      <h2 class="section">${say.owner.members}</h2>
      ${members.length <= HANDFUL ? nothing : html`
        <div class="searchfield input">${icons.search()}
          <input type="search" name="member-search" aria-label=${say.owner.findMembers} placeholder=${say.owner.findMembers} autocomplete="off" maxlength=${limits().nameChars}
            .value=${query} @input=${(e: Event) => { query = (e.target as HTMLInputElement).value; update(); }}></div>`}
      ${shownMembers().length === 0 ? html`<p class="hint" data-no-member>${say.owner.noMember}</p>` : html`<ul class="list">${shownMembers().map(memberRow)}</ul>`}
      ${query.trim() !== '' || moreMembers || members.length <= HANDFUL ? nothing : html`<button class="btn secondary" type="button" data-more-members @click=${() => { moreMembers = true; update(); }}>${say.friends.seeMoreCount(members.length - HANDFUL)}</button>`}
    </div>`;
  const update = () => render(view(), root);

  const show = (issued: Issued, note: string) => { link = { url: origin + issued.path, note }; copied = ''; error = null; };

  async function reload(): Promise<void> {
    const [i, m, r] = await Promise.all([api.invites(), api.members(), api.signUpRequests()]);
    if (i.ok) invites = i.data; else error = i;
    if (m.ok) members = m.data; else error = m;
    if (r.ok) { signUpOpen = r.data.open; requests = r.data.requests; } else error = r;
    update();
  }

  /** Approve, refuse or delete a request; then everything again (an approved one is now a member). */
  async function answer(act: () => Promise<{ ok: true } | Failure>): Promise<void> {
    error = null;
    const result = await act();
    if (!result.ok) error = result;
    await reload();
  }

  async function setOpen(open: boolean): Promise<void> {
    error = null;
    const result = await api.setSignUpOpen(open);
    if (result.ok) signUpOpen = open; else error = result;
    update();
  }

  async function makeInvite(): Promise<void> {
    const result = await api.createInvite({ days, uses });
    if (!result.ok) { error = result; update(); return; }
    show(result.data, say.owner.inviteMade(days, uses === 0 ? say.owner.anyone : uses === 1 ? say.owner.onePerson : say.owner.upTo(uses)));
    await reload();
  }

  async function makeReset(gomId: string): Promise<void> {
    const result = await api.resetLink(gomId);
    if (!result.ok) { error = result; update(); return; }
    show(result.data, say.owner.resetMade);
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
    try { await navigator.clipboard.writeText(link.url); copied = say.friends.copied; } catch { copied = say.friends.copyFailed; }
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
