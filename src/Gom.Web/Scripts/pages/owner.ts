import { html, nothing, render } from 'lit-html';
import type { Api, Failure, Invite, Issued, Member } from '../api.js';
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
      ${m.isOwner || m.disabled ? nothing : html`<button class="btn secondary small" type="button" @click=${() => makeReset(m.gomId)}>${say.owner.makeReset}</button>`}
      ${m.isOwner || m.deleted ? nothing : m.disabled
        ? html`<button class="btn secondary small" type="button" data-enable @click=${() => setDisabled(m.gomId, false)}>${say.owner.enable}</button>`
        : html`<button class="btn secondary small" type="button" data-disable @click=${() => setDisabled(m.gomId, true)}>${say.owner.disable}</button>`}
    </li>`;

  const linkBox = () => link === null ? nothing : html`
    <div class="card">
      <label for="link">${say.owner.linkOnce}</label>
      <div class="input"><input id="link" data-link readonly type="text" .value=${link.url}>
        <button class="ib" type="button" data-copy aria-label=${say.friends.copyLink} @click=${copyLink}>${icons.copy()}</button></div>
      <p class="hint">${link.note}</p>
      <p class="hint" role="status">${copied}</p>
    </div>`;

  /** A handful of members, or everyone who matches what was typed. */
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
    const [i, m] = await Promise.all([api.invites(), api.members()]);
    if (i.ok) invites = i.data; else error = i;
    if (m.ok) members = m.data; else error = m;
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
