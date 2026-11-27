import { html, nothing, render } from 'lit-html';
import type { Api, ContactMessage, Failure, Invite, Issued, JoinRequest, Member } from '../api.js';
import * as icons from '../icons.js';
import { matches } from '../search.js';
import { appBar, errorBox, formatTime } from '../ui.js';
import { limits } from '../limits.js';
import { menuItem, menuPoint, menuView, placeMenu } from '../menu.js';
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
  /** Messages from the privacy page's form, newest first. */
  let contacts: ContactMessage[] = [];
  /** The open "..." menu: whose row, and where it was opened (none on a phone, where it is a sheet). */
  let menu: { kind: 'member' | 'invite'; id: string; at: { x: number; y: number } } | null = null;

  /** The "..." of a row: opens its menu, which closes again to this button. */
  const moreButton = (kind: 'member' | 'invite', id: string, label: string) => html`
    <button class="ib li-more" type="button" data-row-more=${`${kind}:${id}`} aria-label=${label} aria-haspopup="menu"
      aria-expanded=${menu?.kind === kind && menu.id === id ? 'true' : 'false'}
      @click=${(e: MouseEvent) => { menu = { kind, id, at: menuPoint(e) }; update(); root.querySelector<HTMLElement>('[data-row-actions] [role="menuitem"]')?.focus(); }}>${icons.more()}</button>`;

  const inviteRow = (i: Invite) => html`
    <li class="li invite" data-invite=${i.id} data-status=${i.status}>
      <div class="t">
        <b>${STATUS[i.status]}</b>
        <small>${say.owner.inviteLine(i.uses, String(i.maxUses ?? '∞'), formatTime(i.createdAt), i.status === 'expired' ? say.owner.hasExpired : say.owner.expires(formatTime(i.expiresAt)))}</small>
        ${i.members.length === 0 ? nothing : html`<small data-joined>${say.owner.joined(i.members.map((m) => `${m.displayName} (@${m.gomId})`).join(', '))}</small>`}
      </div>
      ${i.status === 'active' ? moreButton('invite', i.id, say.owner.inviteOptions) : nothing}
    </li>`;

  const memberRow = (m: Member) => html`
    <li class="li" data-member=${m.gomId}>
      <div class="t"><b>${m.displayName}${m.isOwner ? html`<span class="tag">${say.owner.ownerTag}</span>` : nothing}${m.deleted ? html`<span class="tag">${say.owner.deletedTag}</span>` : m.disabled ? html`<span class="tag">${say.owner.disabledTag}</span>` : nothing}</b><small>@${m.gomId}</small></div>
      ${m.isOwner || m.deleted ? nothing : moreButton('member', m.gomId, say.friends.optionsFor(m.displayName))}
    </li>`;

  /** The open row's actions: a member's reset link and lock or unlock, or an invite's revoke. Each closes the menu first. */
  const menuOf = () => {
    if (menu === null) return nothing;
    const { kind, id } = menu;
    const pick = (run: () => void) => () => { closeMenu(false); run(); };
    if (kind === 'invite') return menuView(say.owner.inviteOptions, [menuItem('revoke', icons.block(), say.owner.revokeInvite, pick(() => void revoke(id)), true)], () => closeMenu());
    const m = members.find((x) => x.gomId === id);
    if (m === undefined) return nothing;
    return menuView(say.friends.optionsFor(m.displayName), m.disabled
      ? [menuItem('enable', icons.unlock(), say.owner.enable, pick(() => void setDisabled(id, false)))]
      : [menuItem('reset', icons.key(), say.owner.makeReset, pick(() => void makeReset(id))),
        menuItem('disable', icons.lock(), say.owner.disable, pick(() => void setDisabled(id, true)), true)], () => closeMenu());
  };

  /** Closes the menu; by Escape, a tap outside or "Đóng", the focus goes back to the row's "...". */
  function closeMenu(refocus = true): void {
    const was = menu;
    menu = null;
    update();
    if (refocus && was !== null) root.querySelector<HTMLElement>(`[data-row-more="${was.kind}:${was.id}"]`)?.focus();
  }

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

  const contactRow = (c: ContactMessage) => html`
    <li class="li" data-contact=${c.id}>
      <div class="t"><small>${formatTime(c.createdAt)}${c.replyTo === null ? nothing : html` · ${say.owner.contactReply(c.replyTo)}`}</small>
        <span class="request-note">${c.body}</span></div>
      <button class="btn secondary small" type="button" data-delete-contact @click=${() => answer(() => api.deleteContact(c.id))}>${say.owner.remove}</button>
    </li>`;

  /** Shown only when there is something to read: the form is on the privacy page, and most days nobody writes. */
  const contactsView = () => contacts.length === 0 ? nothing : html`
    <h2 class="section">${say.owner.contactHeading}</h2>
    <p class="hint">${say.owner.contactNote(limits().contactKeepDays)}</p>
    <ul class="list" data-contact-list>${contacts.map(contactRow)}</ul>`;

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
      ${contactsView()}
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
    </div>
    ${menuOf()}`;
  const update = () => { render(view(), root); placeMenu(root.querySelector<HTMLElement>('[data-row-actions]'), menu?.at); };

  const show = (issued: Issued, note: string) => { link = { url: origin + issued.path, note }; copied = ''; error = null; };

  async function reload(): Promise<void> {
    const [i, m, r, c] = await Promise.all([api.invites(), api.members(), api.signUpRequests(), api.contactMessages()]);
    if (i.ok) invites = i.data; else error = i;
    if (m.ok) members = m.data; else error = m;
    if (r.ok) { signUpOpen = r.data.open; requests = r.data.requests; } else error = r;
    if (c.ok) contacts = c.data.messages; else error = c;
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
