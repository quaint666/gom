import { html, nothing, render } from 'lit-html';
import type { Api, ContactMessage, CustomPack, Failure, Invite, Issued, JoinRequest, Member } from '../api.js';
import * as icons from '../icons.js';
import { matches } from '../search.js';
import { errorBox, formatTime } from '../ui.js';
import { limits } from '../limits.js';
import { menuItem, menuPoint, menuView, placeMenu } from '../menu.js';
import { say } from '../say.js';
import { stickerUrl } from '../stickers.js';

/**
 * The owner's parts of Settings, each a page of its own at /settings/{code} (H: one long page made the lists hard to read and easy to
 * press wrong). Storage is the sixth (storage.ts). Pages.cs and settings.ts list the same codes.
 */
export const OWNER_PARTS = ['invites', 'requests', 'members', 'stickers', 'contact'] as const;
export type OwnerPart = typeof OWNER_PARTS[number];

/** What the Settings menu says under each owner part, asked for when it is drawn and again after a change. */
export interface OwnerCounts { activeInvites: number; waiting: number; signUpOpen: boolean; members: number; packs: number; contacts: number }

export async function ownerCounts(api: Api): Promise<OwnerCounts | null> {
  const [i, r, m, p, c] = await Promise.all([api.invites(), api.signUpRequests(), api.members(), api.stickerPacks(), api.contactMessages()]);
  if (!i.ok || !r.ok || !m.ok || !p.ok || !c.ok) return null;
  return {
    activeInvites: i.data.filter((x) => x.status === 'active').length,
    waiting: r.data.requests.filter((x) => !x.refused).length, signUpOpen: r.data.open,
    members: m.data.filter((x) => !x.deleted).length, packs: p.data.length, contacts: c.data.messages.length,
  };
}

const STATUS: Record<Invite['status'], string> = { active: say.owner.active, full: say.owner.full, revoked: say.owner.revoked, expired: say.owner.expired };

/** What the owner may choose for a new link: how long it works, and how many people it lets in (0 is any number). */
const DAYS = [1, 7, 30] as const;
const USES = [1, 10, 0] as const;

/**
 * One owner part: make invites, answer requests to join, see members (a reset link, lock), sticker packs, or the contact form's
 * messages. `changed` is told after anything that changes a count in the Settings menu.
 */
export function mountOwner(root: HTMLElement, api: Api, go: (path: string) => void, origin: string, part: OwnerPart, changed: () => void = () => {}): void {
  let invites: Invite[] = [];
  let members: Member[] = [];
  let link: { url: string; note: string } | null = null;
  let copied = '';
  let error: Failure | null = null;
  let query = '';
  /** Members: everyone, or only the locked ones. */
  let only: 'all' | 'locked' = 'all';
  let days: number = limits().inviteDefaultDays;
  let uses: number = limits().inviteDefaultUses;
  /** The owner's groups that new people can be put in (they lead it or are a deputy), and the one chosen for a link or for approving. */
  let groups: { id: string; title: string }[] = [];
  let group = '';
  let approveInto = '';
  /** Asking to join: whether it is open, and who asked (waiting first, then refused). */
  let signUpOpen = false;
  let requests: JoinRequest[] = [];
  /** The request being refused: asked first, so a refusal is never one stray tap. */
  let refusing: string | null = null;
  /** Messages from the privacy page's form, newest first. */
  let contacts: ContactMessage[] = [];
  /** The open "..." menu: whose row, and where it was opened (none on a phone, where it is a sheet). */
  let menu: { kind: 'member' | 'invite'; id: string; at: { x: number; y: number } } | null = null;
  /** Sticker packs made here: the packs, the name of a new one, the pack being deleted (asked first), and what went wrong. */
  let packs: CustomPack[] = [];
  let packName = '';
  let deletingPack: string | null = null;
  let packError: Failure | null = null;
  let packBusy = false;
  /**
   * Where each pack shows, as being chosen (saved with its own button): the people who have it wherever they talk, and the owner's
   * chats where everyone has it; who and which chats there are to choose from; what was typed to find a person; the pack just saved.
   */
  type Where = { everywhere: boolean; chats: string[]; people: string[] };
  let where: Record<string, Where> = {};
  let chats: { id: string; title: string }[] = [];
  let people: { gomId: string; name: string }[] = [];
  let peopleQuery: Record<string, string> = {};
  let whereSaved: string | null = null;

  /** Sticker packs (H): make one, add pictures to it, take one out, or delete a pack. Stickers already sent still show. */
  const packsView = () => html`
    <p class="hint">${say.owner.stickersNote}</p>
    ${errorBox(packError)}
    <form class="card" data-new-pack @submit=${(e: Event) => { e.preventDefault(); void createPack(); }}>
      <label for="pack-name">${say.owner.packName}</label>
      <div class="input"><input id="pack-name" name="pack-name" type="text" maxlength=${limits().stickerPackChars} autocomplete="off" required .value=${packName}
        @input=${(e: Event) => { packName = (e.target as HTMLInputElement).value; }}></div>
      <button class="btn" type="submit" ?disabled=${packBusy}>${say.owner.makePack}</button>
    </form>
    ${packs.map((p) => html`
      <div class="card" data-pack=${p.id}>
        <div class="pack-head"><b>${p.name}</b><small>${say.owner.stickerCount(p.stickers.length)}</small></div>
        ${p.stickers.length === 0 ? nothing : html`<div class="stk-grid">${p.stickers.map((s) => html`
          <div class="stk-own" data-pack-sticker=${s.id}><img src=${stickerUrl(s.code)} alt="" width="56" height="56" loading="lazy" draggable="false">
            <button class="ib tray-x" type="button" aria-label=${say.owner.removeSticker} data-remove-sticker @click=${() => void removeSticker(s.id)}>${icons.close()}</button></div>`)}</div>`}
        ${whereView(p.id)}
        <label class="btn secondary ${packBusy ? 'busy' : ''}" data-add-stickers>${icons.image()} ${say.owner.addStickers}
          <input class="file-input" type="file" accept="image/png,image/webp,image/jpeg,image/gif" multiple ?disabled=${packBusy}
            @change=${(e: Event) => { const input = e.target as HTMLInputElement; void addStickers(p.id, [...(input.files ?? [])]); input.value = ''; }}></label>
        ${deletingPack === p.id ? html`
          <div class="ask" role="group" data-delete-pack-ask><p>${say.owner.deletePackAsk(p.name)}</p>
            <button class="btn small danger" type="button" data-delete-pack-yes @click=${() => void deletePack(p.id)}>${say.owner.deletePack}</button>
            <button class="btn secondary small" type="button" data-delete-pack-no @click=${() => { deletingPack = null; update(); }}>${say.common.no}</button></div>`
        : html`<button class="btn secondary danger-text" type="button" data-delete-pack @click=${() => { deletingPack = p.id; update(); }}>${say.owner.deletePack}</button>`}
      </div>`)}`;

  /**
   * Every chat, or only for the people and in the chats chosen (the owner's own chats: they cannot see others); saved with its button.
   * A person chosen has the pack in every chat they are in, even one between two others.
   */
  const whereView = (pack: string) => {
    const w = where[pack] ?? { everywhere: true, chats: [], people: [] };
    const set = (next: Where) => { where = { ...where, [pack]: next }; whereSaved = null; update(); };
    const query = peopleQuery[pack] ?? '';
    const found = people.filter((p) => query.trim() === '' || matches(`${p.name} ${p.gomId}`, query));
    return html`
      <fieldset class="pack-where" data-pack-where=${pack}>
        <legend>${say.owner.packWhere}</legend>
        <label class="check"><input type="radio" name=${`where-${pack}`} data-where-everywhere .checked=${w.everywhere} @change=${() => set({ ...w, everywhere: true })}> ${say.owner.everywhere}</label>
        <label class="check"><input type="radio" name=${`where-${pack}`} data-where-chosen .checked=${!w.everywhere} @change=${() => set({ ...w, everywhere: false })}> ${say.owner.onlyChosen}</label>
        ${w.everywhere ? nothing : html`
          <p class="pack-sub">${say.owner.chosenPeople}</p>
          <div class="searchfield input">${icons.search()}
            <input type="search" aria-label=${say.owner.findMembers} placeholder=${say.owner.findMembers} autocomplete="off" maxlength=${limits().nameChars} data-where-find
              .value=${query} @input=${(e: Event) => { peopleQuery = { ...peopleQuery, [pack]: (e.target as HTMLInputElement).value }; update(); }}></div>
          <ul class="pack-chats">${found.map((p) => html`<li><label class="check"><input type="checkbox" data-where-person=${p.gomId} .checked=${w.people.includes(p.gomId)}
            @change=${(e: Event) => set({ ...w, people: (e.target as HTMLInputElement).checked ? [...w.people, p.gomId] : w.people.filter((x) => x !== p.gomId) })}> ${p.name} <small>@${p.gomId}</small></label></li>`)}</ul>
          <p class="pack-sub">${say.owner.chosenChats}</p>
          <ul class="pack-chats">${chats.map((c) => html`<li><label class="check"><input type="checkbox" data-where-chat=${c.id} .checked=${w.chats.includes(c.id)}
            @change=${(e: Event) => set({ ...w, chats: (e.target as HTMLInputElement).checked ? [...w.chats, c.id] : w.chats.filter((x) => x !== c.id) })}> ${c.title}</label></li>`)}</ul>
          ${w.chats.length === 0 && w.people.length === 0 ? html`<p class="hint" data-where-none>${say.owner.noneChosen}</p>` : nothing}`}
        <button class="btn secondary small" type="button" data-where-save ?disabled=${packBusy} @click=${() => void saveWhere(pack)}>${say.owner.saveWhere}</button>
        ${whereSaved === pack ? html`<span class="hint" role="status">${say.owner.whereSaved}</span>` : nothing}
      </fieldset>`;
  };

  async function saveWhere(pack: string): Promise<void> {
    const w = where[pack] ?? { everywhere: true, chats: [], people: [] };
    packError = null;
    const result = await api.setPackWhere(pack, w.everywhere ? { everywhere: true, chats: [], people: [] } : w);
    if (!result.ok) { packError = result; update(); return; }
    whereSaved = pack;
    update();
  }

  async function loadPacks(): Promise<void> {
    const [result, list, everyone] = await Promise.all([api.stickerPacks(), api.conversations(), api.members()]);
    if (result.ok) {
      packs = result.data;
      where = Object.fromEntries(packs.map((p) => [p.id, { everywhere: p.everywhere !== false, chats: p.chats ?? [], people: p.people ?? [] }]));
    } else packError = result;
    if (list.ok) chats = list.data.filter((c) => !c.hidden).map((c) => ({ id: c.id, title: c.title ?? '…' }));
    // Members still using Gom; the owner has every pack already.
    if (everyone.ok) people = everyone.data.filter((m) => !m.isOwner && !m.disabled && !m.deleted).map((m) => ({ gomId: m.gomId, name: m.displayName }));
    update();
  }

  async function createPack(): Promise<void> {
    if (packBusy) return;
    packBusy = true; packError = null; update();
    const result = await api.createStickerPack(packName);
    packBusy = false;
    if (!result.ok) { packError = result; update(); return; }
    packName = '';
    await loadPacks();
    changed();
  }

  /** One picture at a time, in the order picked; the first that fails says why, and the rest still go. */
  async function addStickers(pack: string, files: File[]): Promise<void> {
    if (packBusy || files.length === 0) return;
    packBusy = true; packError = null; update();
    for (const file of files) {
      const result = await api.addSticker(pack, file);
      if (!result.ok) packError ??= result;
    }
    packBusy = false;
    await loadPacks();
  }

  async function removeSticker(id: string): Promise<void> {
    const result = await api.deleteSticker(id);
    if (!result.ok) { packError = result; update(); return; }
    await loadPacks();
  }

  async function deletePack(id: string): Promise<void> {
    const result = await api.deleteStickerPack(id);
    deletingPack = null;
    if (!result.ok) { packError = result; update(); return; }
    await loadPacks();
    changed();
  }

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
        ${i.group ? html`<small data-invite-group>${say.owner.intoGroup(i.group.title ?? '…')}</small>` : nothing}
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

  /** A request, with all its note: Approve is the button; Refuse asks first, in the row. */
  const requestRow = (r: JoinRequest) => html`
    <li class="li request" data-join-request=${r.gomId}>
      <div class="t"><b>${r.displayName}</b><small>@${r.gomId} · ${say.owner.askedAt(formatTime(r.requestedAt))}</small>
        <span class="request-note">${r.note}</span></div>
      ${refusing === r.gomId ? html`
        <div class="ask" role="group" data-refuse-ask><p>${say.owner.refuseAsk(r.displayName)}</p>
          <button class="btn small danger" type="button" data-refuse-yes @click=${() => { refusing = null; void answer(() => api.refuseJoin(r.gomId)); }}>${say.owner.refuse}</button>
          <button class="btn secondary small" type="button" data-refuse-no @click=${() => { refusing = null; update(); }}>${say.common.no}</button></div>`
      : html`
        <div class="acts">
          <button class="btn small" type="button" data-approve @click=${() => answer(() => approveInto === '' ? api.approveJoin(r.gomId) : api.approveJoin(r.gomId, approveInto))}>${say.owner.approve}</button>
          ${r.refused
            ? html`<button class="btn secondary small" type="button" data-delete-request @click=${() => answer(() => api.deleteJoin(r.gomId))}>${say.owner.remove}</button>`
            : html`<button class="btn secondary small" type="button" data-refuse @click=${() => { refusing = r.gomId; update(); }}>${say.owner.refuse}</button>`}
        </div>`}
    </li>`;

  /** A choice of one of the owner's groups, or none; shown only when there is a group to choose. */
  const groupField = (id: string, label: string, value: string, pick: (v: string) => void) => groups.length === 0 ? nothing : html`
    <div class="field"><label for=${id}>${label}</label>
      <select id=${id} name=${id} data-group-choice=${id} @change=${(e: Event) => pick((e.target as HTMLSelectElement).value)}>
        <option value="" ?selected=${value === ''}>${say.owner.noGroup}</option>
        ${groups.map((g) => html`<option value=${g.id} ?selected=${g.id === value}>${g.title}</option>`)}</select></div>`;

  /** A list that starts folded: past links, refused requests. Its count is in its title, so nothing hides unseen. */
  const folded = (title: string, attr: string, body: unknown) => html`
    <details class="fold" data-fold=${attr}><summary>${icons.chevron()}<span>${title}</span></summary>${body}</details>`;

  const requestsView = () => {
    const waiting = requests.filter((r) => !r.refused);
    const refused = requests.filter((r) => r.refused);
    return html`
      <div class="pick" data-sign-up-switch><input type="checkbox" id="sign-up-open" data-sign-up-open .checked=${signUpOpen}
        @change=${(e: Event) => void setOpen((e.target as HTMLInputElement).checked)}><label for="sign-up-open">${say.owner.openSignUp}</label></div>
      ${waiting.length === 0 && !signUpOpen ? nothing : html`
        <h2 class="section">${say.owner.waitingCount(waiting.length)}</h2>
        ${waiting.length === 0 ? nothing : groupField('approve-into', say.owner.approveInto, approveInto, (v) => { approveInto = v; })}
        ${waiting.length === 0 ? html`<p class="hint" data-none-waiting>${say.owner.noneWaiting}</p>` : html`<ul class="list" data-waiting-list>${waiting.map(requestRow)}</ul>`}`}
      ${refused.length === 0 ? nothing : folded(say.owner.refusedCount(refused.length), 'refused', html`<ul class="list" data-refused-list>${refused.map(requestRow)}</ul>`)}`;
  };

  const contactRow = (c: ContactMessage) => html`
    <li class="li" data-contact=${c.id}>
      <div class="t"><small>${formatTime(c.createdAt)}${c.replyTo === null ? nothing : html` · ${say.owner.contactReply(c.replyTo)}`}</small>
        <span class="request-note">${c.body}</span></div>
      <button class="btn secondary small" type="button" data-delete-contact @click=${() => answer(() => api.deleteContact(c.id))}>${say.owner.remove}</button>
    </li>`;

  const contactsView = () => html`
    <p class="hint">${say.owner.contactNote(limits().contactKeepDays)}</p>
    ${contacts.length === 0 ? html`<p class="hint" data-no-contact>${say.owner.noContact}</p>` : html`<ul class="list" data-contact-list>${contacts.map(contactRow)}</ul>`}`;

  const invitesView = () => {
    const active = invites.filter((i) => i.status === 'active');
    const past = invites.filter((i) => i.status !== 'active');
    return html`
      <div class="card invite-form">
        <div class="field"><label for="invite-days">${say.owner.validFor}</label>
          <select id="invite-days" name="days" @change=${(e: Event) => { days = Number((e.target as HTMLSelectElement).value); }}>
            ${DAYS.map((d) => html`<option value=${d} ?selected=${d === days}>${say.owner.days(d)}</option>`)}</select></div>
        <div class="field"><label for="invite-uses">${say.owner.letsIn}</label>
          <select id="invite-uses" name="uses" @change=${(e: Event) => { uses = Number((e.target as HTMLSelectElement).value); }}>
            ${USES.map((u) => html`<option value=${u} ?selected=${u === uses}>${u === 0 ? say.owner.anyNumber : say.owner.people(u)}</option>`)}</select></div>
        ${groupField('invite-group', say.owner.addToGroup, group, (v) => { group = v; })}
        <button class="btn" type="button" data-create-invite @click=${makeInvite}>${say.owner.makeInvite}</button>
      </div>
      ${linkBox()}
      ${active.length === 0 ? html`<p class="hint" data-no-active>${say.owner.noActive}</p>` : html`<ul class="list" data-active-invites>${active.map(inviteRow)}</ul>`}
      ${past.length === 0 ? nothing : folded(say.owner.pastLinks(past.length), 'past', html`<ul class="list" data-past-invites>${past.map(inviteRow)}</ul>`)}`;
  };

  /** Everyone, or the locked ones, narrowed by what was typed (without accents). */
  const shownMembers = (): Member[] => members.filter((m) => (only === 'all' || m.disabled) && (query.trim() === '' || matches(`${m.displayName} ${m.gomId}`, query)));

  const membersView = () => {
    const tab = (id: 'all' | 'locked', label: string, count: number) => html`
      <button class="tab" type="button" role="tab" data-member-filter=${id} aria-selected=${only === id ? 'true' : 'false'}
        @click=${() => { only = id; update(); }}>${label} <span class="count">${count}</span></button>`;
    const shown = shownMembers();
    return html`
      ${linkBox()}
      <div class="tabs" role="tablist" aria-label=${say.owner.members}>${tab('all', say.owner.everyone, members.length)}${tab('locked', say.owner.disabledTag, members.filter((m) => m.disabled).length)}</div>
      <div class="searchfield input">${icons.search()}
        <input type="search" name="member-search" aria-label=${say.owner.findMembers} placeholder=${say.owner.findMembers} autocomplete="off" maxlength=${limits().nameChars}
          .value=${query} @input=${(e: Event) => { query = (e.target as HTMLInputElement).value; update(); }}></div>
      ${shown.length === 0 ? html`<p class="hint" data-no-member>${say.owner.noMember}</p>` : html`<ul class="list" data-member-list>${shown.map(memberRow)}</ul>`}`;
  };

  const body = () => {
    switch (part) {
      case 'invites': return invitesView();
      case 'requests': return requestsView();
      case 'members': return membersView();
      case 'stickers': return packsView();
      case 'contact': return contactsView();
    }
  };

  const view = () => html`<div class="page owner-part" data-owner-part=${part}>${errorBox(error)}${body()}</div>${menuOf()}`;
  const update = () => { render(view(), root); placeMenu(root.querySelector<HTMLElement>('[data-row-actions]'), menu?.at); };

  const show = (issued: Issued, note: string) => { link = { url: origin + issued.path, note }; copied = ''; error = null; };

  /** What this part shows, and nothing else. */
  async function reload(): Promise<void> {
    if (part === 'stickers') { await loadPacks(); return; }
    if (part === 'invites') { const i = await api.invites(); if (i.ok) invites = i.data; else error = i; }
    if (part === 'invites' || part === 'requests') {
      const c = await api.conversations();
      if (c.ok) groups = c.data.filter((x) => x.kind === 'group' && (x.role === 'leader' || x.role === 'deputy')).map((x) => ({ id: x.id, title: x.title ?? '…' }));
    }
    if (part === 'members') { const m = await api.members(); if (m.ok) members = m.data; else error = m; }
    if (part === 'requests') { const r = await api.signUpRequests(); if (r.ok) { signUpOpen = r.data.open; requests = r.data.requests; } else error = r; }
    if (part === 'contact') { const c = await api.contactMessages(); if (c.ok) contacts = c.data.messages; else error = c; }
    update();
  }

  /** Approve, refuse or delete a request, or delete a message; then the list again, and the menu's count. */
  async function answer(act: () => Promise<{ ok: true } | Failure>): Promise<void> {
    error = null;
    const result = await act();
    if (!result.ok) error = result;
    await reload();
    changed();
  }

  async function setOpen(open: boolean): Promise<void> {
    error = null;
    const result = await api.setSignUpOpen(open);
    if (result.ok) signUpOpen = open; else error = result;
    update();
    changed();
  }

  async function makeInvite(): Promise<void> {
    const result = await api.createInvite(group === '' ? { days, uses } : { days, uses, group });
    if (!result.ok) { error = result; update(); return; }
    const into = groups.find((g) => g.id === group);
    show(result.data, say.owner.inviteMade(days, uses === 0 ? say.owner.anyone : uses === 1 ? say.owner.onePerson : say.owner.upTo(uses))
      + (into === undefined ? '' : ` ${say.owner.inviteGroup(into.title)}`));
    await reload();
    changed();
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
    changed();
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
