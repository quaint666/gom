import { html, nothing, type TemplateResult } from 'lit-html';
import { live } from 'lit-html/directives/live.js';
import type { Api, Conversation, Failure, GroupMember, Person } from '../api.js';
import * as icons from '../icons.js';
import { limits } from '../limits.js';
import { menuItem, menuPoint, menuView, placeMenu } from '../menu.js';
import { matches } from '../search.js';
import { say } from '../say.js';
import { errorBox, avatar } from '../ui.js';

/** What the group pane needs from the conversation page around it: the lists it keeps, and the bits of the old panel it still shows. */
export interface GroupPaneContext {
  root: HTMLElement;
  api: Api;
  update(): void;
  /** A person's card, from a tap on a member. */
  openCard(userId: string): void;
  members(conversation: string): GroupMember[] | undefined;
  setMembers(conversation: string, members: GroupMember[]): void;
  /** The conversation list again (a new name or picture arrives with it). */
  refreshList(): Promise<void>;
  /** Leaves the group (the server first, then the list and the page). */
  leave(conversation: string): Promise<void>;
  /** A tap outside a dialog closes it. */
  outside(close: () => void): (e: MouseEvent) => void;
  /** The shared pieces of the conversation's information: what to find in it, what is pinned, pictures and files, disappearing messages. */
  actions(c: Conversation): TemplateResult;
  pins(c: Conversation): TemplateResult | typeof nothing;
  media(c: Conversation): TemplateResult;
  disappear(c: Conversation): TemplateResult;
  /** Something extra at the foot of the rows (the old page's link, until the work moves in). */
  extraRows(c: Conversation, mayManage: boolean): TemplateResult | typeof nothing;
}

type Sub = 'members' | 'media' | 'disappear';
type Dialog =
  | { kind: 'rename'; draft: string }
  | { kind: 'add'; picked: Set<string>; query: string }
  | { kind: 'ask'; ask: 'remove' | 'handover'; member: GroupMember; ban: boolean }
  | { kind: 'leave' };

/**
 * A group's information, in the panel beside the thread or on a phone's page: its picture, name and size, then rows that open inside
 * the same panel (members, pictures and files, disappearing messages), with everything a leader or deputy may do done in place, in small
 * dialogs: change the name or picture, add people, make a deputy, hand over, remove. Nothing here leaves the conversation.
 */
export function createGroupPane(ctx: GroupPaneContext) {
  let current = '';
  let sub: Sub | null = null;
  let dialog: Dialog | null = null;
  let menuFor: { id: string; at: { x: number; y: number } } | null = null;
  let pictureMenu: { at: { x: number; y: number } } | null = null;
  let friends: Person[] | null = null;
  let busy = false;
  let error: Failure | null = null;
  let status = '';

  const reset = (id: string) => { current = id; sub = null; dialog = null; menuFor = null; pictureMenu = null; friends = null; busy = false; error = null; status = ''; };
  const mine = (id: string) => ctx.members(id)?.find((m) => m.isMe);
  const leads = (id: string) => mine(id)?.role === 'leader';
  const mayEdit = (id: string) => mine(id)?.role === 'leader' || mine(id)?.role === 'deputy';
  /** A leader acts on anyone else; a deputy only on plain members. */
  const manages = (id: string, m: GroupMember) => {
    const me = mine(id);
    return !!me && !m.isMe && (me.role === 'leader' || (me.role === 'deputy' && m.role === 'member'));
  };

  /** Runs a change on the server, then shows the group as it now is; a failure stays in view (in the dialog if one is open). */
  async function run(id: string, call: () => Promise<{ ok: true } | Failure>): Promise<boolean> {
    busy = true; error = null; ctx.update();
    const result = await call();
    busy = false;
    if (!result.ok) { error = result; ctx.update(); return false; }
    const fresh = await ctx.api.groupMembers(id);
    if (fresh.ok) ctx.setMembers(id, fresh.data);
    await ctx.refreshList();
    ctx.update();
    return true;
  }

  const close = () => { dialog = null; error = null; ctx.update(); };
  /** A menu opened where it was asked for, with the keyboard in it (so Escape closes it). */
  const showMenu = (at: { x: number; y: number }) => {
    ctx.update();
    const pop = ctx.root.querySelector<HTMLElement>('[data-row-actions]');
    placeMenu(pop, at);
    pop?.querySelector<HTMLElement>('[role="menuitem"]')?.focus();
  };
  const closeMenus = () => { menuFor = null; pictureMenu = null; ctx.update(); };

  const roleTag = (m: GroupMember) => m.role === 'leader' ? html`<span class="tag" data-role="leader">${say.common.leader}</span>`
    : m.role === 'deputy' ? html`<span class="tag" data-role="deputy">${say.common.deputy}</span>` : nothing;

  // ---- the picture

  const picking = () => ctx.root.querySelector<HTMLInputElement>('[data-group-picture-file]')?.click();

  async function setPicture(c: Conversation, file: File): Promise<void> {
    await run(c.id, () => ctx.api.setGroupAvatar(c.id, file).then((r) => r.ok ? { ok: true as const } : r));
  }

  const pictureMenuView = (c: Conversation) => {
    if (pictureMenu === null) return nothing;
    return menuView(say.settings.changePicture, [
      menuItem('group-picture-choose', icons.camera(), c.avatar ? say.settings.changePicture : say.settings.choosePicture, () => { pictureMenu = null; ctx.update(); picking(); }),
      ...(c.avatar ? [menuItem('group-picture-remove', icons.trash(), say.settings.removePicture, () => {
        pictureMenu = null;
        void run(c.id, () => ctx.api.removeGroupAvatar(c.id).then((r) => r.ok ? { ok: true as const } : r));
      }, true)] : [])], closeMenus);
  };

  // ---- the top

  const top = (c: Conversation) => {
    const edit = mayEdit(c.id);
    return html`
      <div class="info-top gtop" data-group-top>
        ${edit ? html`<button class="ib gtop-edit" type="button" data-group-rename aria-label=${say.chat.renameGroup}
          @click=${() => { dialog = { kind: 'rename', draft: c.title ?? '' }; error = null; ctx.update(); }}>${icons.pencil()}</button>` : nothing}
        <div class="gtop-pic">
          ${avatar(c.title ?? '?', false, true, c.avatar)}
          ${edit ? html`
            <button class="gtop-cam" type="button" data-group-picture aria-haspopup="menu" aria-label=${c.avatar ? say.settings.changePicture : say.settings.choosePicture}
              @click=${(e: MouseEvent) => { pictureMenu = { at: menuPoint(e) }; showMenu(pictureMenu.at); }}>${icons.camera()}</button>
            <input class="file-input" type="file" accept="image/jpeg,image/png,image/webp" data-group-picture-file ?disabled=${busy} tabindex="-1" aria-hidden="true"
              @change=${(e: Event) => { const input = e.target as HTMLInputElement; const file = input.files?.[0]; input.value = ''; if (file) void setPicture(c, file); }}>` : nothing}
        </div>
        <h2 data-group-name>${c.title ?? '…'}</h2>
        <small>${say.common.members(c.memberCount ?? 0)}</small>
      </div>`;
  };

  // ---- the rows

  const row = (key: string, icon: TemplateResult, label: string, value: string | null, open: () => void, danger = false) => html`
    <li><button class=${danger ? 'danger' : ''} type="button" data-group-row=${key} @click=${open}>
      <span class="lead-i">${icon}</span><span class="t"><b>${label}</b></span>${value === null ? nothing : html`<span class="gval">${value}</span>`}
      <span class="chev">${icons.chevron()}</span></button></li>`;

  const rest = (c: Conversation) => {
    const members = ctx.members(c.id);
    const off = c.disappearSeconds == null;
    return html`
      ${top(c)}
      ${ctx.actions(c)}
      ${ctx.pins(c)}
      <ul class="nav gnav" data-group-rows>
        ${row('members', icons.users(), say.chat.membersHeading, String(members?.length ?? c.memberCount ?? 0), () => { sub = 'members'; ctx.update(); })}
        ${row('media', icons.image(), say.chat.mediaRow, null, () => { sub = 'media'; ctx.update(); })}
        ${row('disappear', icons.clock(), say.chat.disappear, off ? say.chat.disappearOffChoice : say.chat.lasts(c.disappearSeconds!), () => { sub = 'disappear'; ctx.update(); })}
        ${ctx.extraRows(c, mayEdit(c.id))}
        ${row('leave', icons.signOut(), say.common.leaveGroup, null, () => { dialog = { kind: 'leave' }; error = null; ctx.update(); }, true)}
      </ul>`;
  };

  // ---- members

  const memberRow = (c: Conversation, m: GroupMember) => {
    const inside = html`${avatar(m.displayName, true, false, m.avatar)}
      <span class="gm-text"><span class="gm-line"><b class="gm-name">${m.displayName}${m.isMe ? say.chat.youSuffix : ''}</b>${roleTag(m)}</span>${m.gomId ? html`<small>@${m.gomId}</small>` : nothing}</span>`;
    return html`<li class="gm" data-info-member=${m.id}>
      ${m.isMe ? html`<div class="gm-main">${inside}</div>`
        : html`<button class="gm-main" type="button" data-open-card=${m.id} @click=${() => ctx.openCard(m.id)}>${inside}</button>`}
      <span class="gm-slot">${manages(c.id, m) ? html`<button class="ib" type="button" data-manage=${m.id} aria-haspopup="menu" aria-label=${say.group.manage(m.displayName)}
        aria-expanded=${menuFor?.id === m.id ? 'true' : 'false'}
        @click=${(e: MouseEvent) => { menuFor = { id: m.id, at: menuPoint(e) }; showMenu(menuFor.at); }}>${icons.more()}</button>` : nothing}</span>
    </li>`;
  };

  const membersView = (c: Conversation) => html`
    <button class="gadd" type="button" data-group-add @click=${() => void openAdd()}>
      <span class="lead-i">${icons.users()}</span><b>${say.chat.addPeople}</b></button>
    ${status === '' ? nothing : html`<p class="hint" role="status" data-add-status>${status}</p>`}
    <ul class="info-list gmembers">${(ctx.members(c.id) ?? []).map((m) => memberRow(c, m))}</ul>`;

  const memberMenu = (c: Conversation) => {
    const m = menuFor === null ? undefined : ctx.members(c.id)?.find((x) => x.id === menuFor!.id);
    if (m === undefined || !manages(c.id, m)) return nothing;
    return menuView(say.group.manage(m.displayName), [
      ...(leads(c.id) && m.role === 'member' ? [menuItem('make-deputy', icons.shield(), say.group.makeDeputy, () => { menuFor = null; void run(c.id, () => ctx.api.setRole(c.id, m.id, 'deputy')); })] : []),
      ...(leads(c.id) && m.role === 'deputy' ? [menuItem('drop-deputy', icons.userMinus(), say.group.dropDeputy, () => { menuFor = null; void run(c.id, () => ctx.api.setRole(c.id, m.id, 'member')); })] : []),
      ...(leads(c.id) ? [menuItem('hand-over', icons.key(), say.group.handOver, () => { menuFor = null; dialog = { kind: 'ask', ask: 'handover', member: m, ban: false }; error = null; ctx.update(); })] : []),
      menuItem('remove', icons.close(), say.group.remove, () => { menuFor = null; dialog = { kind: 'ask', ask: 'remove', member: m, ban: false }; error = null; ctx.update(); }, true)], closeMenus);
  };

  // ---- the dialogs

  const dlg = (label: string, body: TemplateResult) => html`
    <dialog class="pcard gdlg" aria-labelledby="gd-title" data-group-dialog data-modal @close=${close} @click=${ctx.outside(close)}>
      <div class="pcard-head gd-head"><h2 id="gd-title">${label}</h2>
        <button class="ib" type="button" data-gd-close aria-label=${say.common.close} @click=${close}>${icons.close()}</button></div>
      ${errorBox(error)}${body}
    </dialog>`;

  async function openAdd(): Promise<void> {
    dialog = { kind: 'add', picked: new Set(), query: '' }; error = null;
    ctx.update();
    if (friends !== null) return;
    const result = await ctx.api.friends();
    if (result.ok) friends = result.data.friends; else error = result;
    ctx.update();
  }

  /** Your friends who are not in the group yet (a member shows with a Gom ID, so the Gom ID is how we tell). */
  const addable = (id: string): Person[] => {
    const inside = new Set((ctx.members(id) ?? []).map((m) => m.gomId).filter((g): g is string => g !== null));
    return (friends ?? []).filter((f) => !inside.has(f.gomId));
  };

  async function addPicked(c: Conversation, d: Extract<Dialog, { kind: 'add' }>): Promise<void> {
    let pending = false;
    const ok = await run(c.id, async () => {
      for (const gomId of d.picked) {
        const result = await ctx.api.addMember(c.id, gomId);
        if (!result.ok) return result;
        if (result.data?.status === 'pending') pending = true;
      }
      return { ok: true as const };
    });
    if (!ok) return;
    status = pending ? say.group.askedApproval : '';
    dialog = null;
    ctx.update();
  }

  const addDialog = (c: Conversation, d: Extract<Dialog, { kind: 'add' }>) => {
    const all = addable(c.id);
    const shown = all.filter((f) => d.query.trim() === '' || matches(`${f.displayName} ${f.gomId}`, d.query));
    return dlg(say.chat.addPeople, html`
      ${friends === null && error === null ? html`<p class="hint" role="status">${say.common.loading}</p>` : all.length === 0 && friends !== null ? html`<p class="hint" data-add-none>${say.chat.addNone}</p>` : html`
        <div class="gsearch">${icons.search()}<input type="search" data-add-search autocomplete="off" placeholder=${say.chat.addSearch} aria-label=${say.chat.addSearch}
          .value=${live(d.query)} @input=${(e: Event) => { d.query = (e.target as HTMLInputElement).value; ctx.update(); }}></div>
        <ul class="info-list gpick">${shown.map((f) => html`
          <li><label class="gpick-row" data-addable=${f.gomId}>
            ${avatar(f.displayName, true, false, f.avatar)}
            <span class="gm-text"><b class="gm-name">${f.displayName}</b><small>@${f.gomId}</small></span>
            <input type="checkbox" .checked=${live(d.picked.has(f.gomId))}
              @change=${(e: Event) => { if ((e.target as HTMLInputElement).checked) d.picked.add(f.gomId); else d.picked.delete(f.gomId); ctx.update(); }}></label></li>`)}</ul>`}
      ${c.joinApproval ? html`<p class="hint">${say.chat.addNeedsApproval}</p>` : nothing}
      <div class="gd-foot"><button class="btn secondary" type="button" data-gd-cancel @click=${close}>${say.common.cancel}</button>
        <button class="btn" type="button" data-add-confirm ?disabled=${busy || d.picked.size === 0} @click=${() => void addPicked(c, d)}>${d.picked.size === 0 ? say.group.add : say.chat.addConfirm(d.picked.size)}</button></div>`);
  };

  const renameDialog = (c: Conversation, d: Extract<Dialog, { kind: 'rename' }>) => {
    const wanted = d.draft.trim();
    const save = async (e: Event) => {
      e.preventDefault();
      if (busy || wanted === '' || wanted === c.title) return;
      if (await run(c.id, () => ctx.api.renameGroup(c.id, wanted))) { dialog = null; ctx.update(); }
    };
    return dlg(say.chat.renameGroup, html`
      <form data-rename @submit=${save}>
        <label class="sr-only" for="group-name">${say.group.name}</label>
        <div class="input"><input id="group-name" name="title" type="text" maxlength=${limits().nameChars} autocomplete="off" .value=${live(d.draft)}
          @input=${(e: Event) => { d.draft = (e.target as HTMLInputElement).value; ctx.update(); }}></div>
        <div class="gd-foot"><button class="btn secondary" type="button" data-gd-cancel @click=${close}>${say.common.cancel}</button>
          <button class="btn" type="submit" ?disabled=${busy || wanted === '' || wanted === c.title}>${say.settings.saveName}</button></div>
      </form>`);
  };

  const askDialog = (c: Conversation, d: Extract<Dialog, { kind: 'ask' }>) => {
    const who = d.member.displayName;
    const text = d.ask === 'remove' ? { q: say.group.removeQ(who), hint: say.group.removeNote(who), yes: say.group.remove }
      : { q: say.group.handOverQ(who), hint: say.group.handOverNote, yes: say.group.handOverYes };
    const go = async () => {
      const call = d.ask === 'remove' ? () => ctx.api.removeMember(c.id, d.member.id, d.ban) : () => ctx.api.handOver(c.id, d.member.id);
      if (await run(c.id, call)) { dialog = null; ctx.update(); }
    };
    return dlg(text.q, html`
      <p class="hint">${text.hint}</p>
      ${d.ask === 'remove' ? html`<label class="check"><input type="checkbox" data-ban .checked=${live(d.ban)} @change=${(e: Event) => { d.ban = (e.target as HTMLInputElement).checked; }}> ${say.group.ban}</label>` : nothing}
      <div class="gd-foot"><button class="btn secondary" type="button" data-cancel-sure @click=${close}>${say.common.cancel}</button>
        <button class=${`btn ${d.ask === 'remove' ? 'danger' : ''}`} type="button" data-confirm-sure ?disabled=${busy} @click=${() => void go()}>${text.yes}</button></div>`);
  };

  const leaveDialog = (c: Conversation) => {
    const me = mine(c.id);
    const others = (ctx.members(c.id)?.length ?? 0) > 1;
    // A leader with others in the group hands over first; a leader alone in it ends the group by leaving, and the question says so.
    if (me?.role === 'leader' && others) return dlg(say.common.leaveGroup, html`
      <p class="hint" data-info-leader-note>${say.chat.leaderMustHandOver}</p>
      <div class="gd-foot"><button class="btn" type="button" data-gd-cancel @click=${close}>${say.common.close}</button></div>`);
    return dlg(say.chat.leaveQ, html`
      <p class="hint">${me?.role === 'leader' ? say.chat.lastOneDeletes : say.group.leaveNote}</p>
      <div class="gd-foot"><button class="btn secondary" type="button" data-info-leave-no @click=${close}>${say.chat.stay}</button>
        <button class="btn danger" type="button" data-info-leave-yes @click=${() => { dialog = null; ctx.update(); void ctx.leave(c.id); }}>${say.common.leaveGroup}</button></div>`);
  };

  // ---- the pane

  const subHead = (label: string) => html`
    <div class="gsub"><button class="ib" type="button" data-group-sub-back aria-label=${say.common.back} @click=${() => { sub = null; menuFor = null; ctx.update(); }}>${icons.back()}</button><b>${label}</b></div>`;

  return {
    /** Whether a part of the panel (members, pictures, disappearing messages) is open instead of the rows. */
    inSub: (): boolean => sub !== null,
    /** What goes inside the information panel for this group. */
    view(c: Conversation): TemplateResult {
      if (c.id !== current) reset(c.id);
      return html`
        ${sub === null ? rest(c) : html`${subHead(sub === 'members' ? say.chat.membersHeading : sub === 'media' ? say.chat.mediaRow : say.chat.disappear)}
          ${sub === 'members' ? membersView(c) : sub === 'media' ? ctx.media(c) : ctx.disappear(c)}`}
        ${dialog === null && error !== null ? errorBox(error) : nothing}`;
    },
    /** The menus and dialogs, drawn beside the page (not inside the scrolling panel). */
    overlays(c: Conversation): TemplateResult {
      if (c.id !== current) return html``;
      return html`${pictureMenuView(c)}${memberMenu(c)}
        ${dialog === null ? nothing : dialog.kind === 'rename' ? renameDialog(c, dialog) : dialog.kind === 'add' ? addDialog(c, dialog)
          : dialog.kind === 'ask' ? askDialog(c, dialog) : leaveDialog(c)}`;
    },
  };
}
