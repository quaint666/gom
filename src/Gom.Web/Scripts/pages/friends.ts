import { html, nothing, render, type TemplateResult } from 'lit-html';
import { keyed } from 'lit-html/directives/keyed.js';
import type { Api, Conversation, FriendLists, Failure, Person, User } from '../api.js';
import * as icons from '../icons.js';
import { fold, matches } from '../search.js';
import { appBar, avatar, errorBox, formatTime } from '../ui.js';
import { limits } from '../limits.js';
import { menuItem, menuPoint, menuView, placeMenu } from '../menu.js';
import { say } from '../say.js';

/** How many friends or groups "Gần đây" shows: the ones you last talked with. Fewer than that, and it would repeat the whole list. */
const RECENT = 5;

const NONE: FriendLists = { friends: [], incoming: [], outgoing: [] };

/** Contacts: friends and groups in two tabs with a search, and adding by ID or by your own link, answering requests, unfriending, blocking. */
export function mountFriends(root: HTMLElement, api: Api, go: (path: string) => void, origin: string): void {
  let user: User | null = null;
  let lists: FriendLists = NONE;
  let blocked: Person[] = [];
  let groups: Conversation[] = [];
  /** When you last talked with each friend (their one-to-one conversation's last message), by Gom ID. */
  let lastTalk = new Map<string, string>();
  let tab: 'friends' | 'groups' = 'friends';
  let query = '';
  /** The friend whose "..." menu (unfriend, block) is open, and where it was opened. */
  let menu: { gomId: string; at: { x: number; y: number } } | null = null;
  let friendId = '';
  let status = '';
  let copied = '';
  let busy = false;
  let error: Failure | null = null;
  /** Unfriending and blocking ask first: one tap on the wrong row is easy. */
  let sure: { kind: 'unfriend' | 'block'; person: Person } | null = null;

  const button = (label: string, attr: string, run: () => void, secondary = true): TemplateResult =>
    html`<button class="btn ${secondary ? 'secondary ' : ''}small" type="button" data-action=${attr} @click=${run}>${label}</button>`;
  const who = (p: Person, extra: TemplateResult | typeof nothing = nothing) =>
    html`<div class="t"><b>${p.displayName}</b><small>@${p.gomId}${extra}</small></div>`;

  /** A titled list, or nothing at all when it is empty. */
  const section = (title: string, rows: TemplateResult[]) => rows.length === 0 ? nothing : html`
    <h2 class="section">${title}</h2>
    <ul class="list">${rows}</ul>`;

  const question = (s: NonNullable<typeof sure>) => {
    const name = s.person.displayName;
    const text = s.kind === 'unfriend'
      ? { q: say.common.unfriendQ(name), hint: say.common.unfriendNote, yes: say.common.unfriend }
      : { q: say.common.blockQ(name), hint: say.common.blockNote(name), yes: say.common.block };
    return html`<li class="card" role="group" aria-labelledby="sure-q" data-sure=${s.person.gomId}>
      <p id="sure-q"><b>${text.q}</b></p><p class="hint">${text.hint}</p>
      <button class="btn" type="button" data-confirm-sure @click=${() => { sure = null; void act(() => s.kind === 'unfriend' ? api.unfriend(s.person.gomId) : api.block(s.person.gomId)); }}>${text.yes}</button>
      <button class="btn secondary" type="button" data-cancel-sure @click=${() => { sure = null; update(); }}>${say.common.cancel}</button>
    </li>`;
  };

  const friendRow = (p: Person) => html`
    <li class="li person" data-friend=${p.gomId}>
      <button class="rowmain" type="button" data-action="message" @click=${() => void message(p.gomId)}>${avatar(p.displayName, false, false, p.avatar)}
        <span class="t"><b>${p.displayName}</b><small>@${p.gomId}</small></span></button>
      <button class="ib li-more" type="button" data-more=${p.gomId} aria-label=${say.friends.optionsFor(p.displayName)} aria-haspopup="menu"
        aria-expanded=${menu?.gomId === p.gomId ? 'true' : 'false'}
        @click=${(e: MouseEvent) => { menu = { gomId: p.gomId, at: menuPoint(e) }; update(); root.querySelector<HTMLElement>('[data-row-actions] [role="menuitem"]')?.focus(); }}>${icons.more()}</button>
    </li>
    ${sure?.person.gomId === p.gomId ? question(sure) : nothing}`;

  /** The open friend's menu; either choice asks first, under the friend's row. */
  const menuOf = () => {
    const p = menu === null ? undefined : lists.friends.find((f) => f.gomId === menu!.gomId);
    if (p === undefined) return nothing;
    const ask = (kind: 'unfriend' | 'block') => () => { menu = null; sure = { kind, person: p }; update(); root.querySelector<HTMLElement>('[data-confirm-sure]')?.focus(); };
    return menuView(say.friends.optionsFor(p.displayName), [
      menuItem('unfriend', icons.userMinus(), say.common.unfriend, ask('unfriend')),
      menuItem('block', icons.block(), say.common.block, ask('block'), true)], closeMenu);
  };

  /** Closes the menu without a choice; the focus goes back to the friend's "...". */
  function closeMenu(): void {
    const was = menu?.gomId;
    menu = null;
    update();
    if (was !== undefined) root.querySelector<HTMLElement>(`[data-more="${was}"]`)?.focus();
  }

  const groupRow = (c: Conversation) => html`
    <li><a class="li person" href="/c/${c.id}" data-group=${c.id}>${avatar(c.title ?? '?')}
      <span class="t"><b>${c.title ?? '…'}</b><small>${say.common.members(c.memberCount ?? 0)}</small></span></a></li>`;

  const tabs = () => {
    const mine = lists.friends.length;
    const tabButton = (id: 'friends' | 'groups', label: string, count: number) => html`
      <button class="tab" type="button" role="tab" id=${`tab-${id}`} data-tab=${id} aria-selected=${tab === id ? 'true' : 'false'}
        @click=${() => { tab = id; menu = null; update(); }}>${label} <span class="count">${count}</span></button>`;
    return html`<div class="tabs" role="tablist" aria-label=${say.common.contacts}>${tabButton('friends', say.friends.friendsTab, mine)}${tabButton('groups', say.friends.groupsTab, groups.length)}</div>`;
  };

  /** A name's letter for the A to Z headings: no accents, no capitals, and "Đ" is its own letter's cousin D. Anything else goes under "#". */
  const letterOf = (name: string): string => { const f = fold(name).charAt(0).toUpperCase(); return /[A-Z]/.test(f) ? f : '#'; };

  const byName = (a: string, b: string) => fold(a).localeCompare(fold(b), 'vi');

  /** Friends A to Z, under a heading for each first letter. */
  const friendList = (rows: Person[]) => {
    const sorted = [...rows].sort((a, b) => byName(a.displayName, b.displayName));
    const letters = [...new Set(sorted.map((p) => letterOf(p.displayName)))].sort((a, b) => a === '#' ? 1 : b === '#' ? -1 : a.localeCompare(b));
    return html`${letters.map((l) => html`<h3 class="letter" data-letter=${l}>${l}</h3>
      <ul class="list">${sorted.filter((p) => letterOf(p.displayName) === l).map(friendRow)}</ul>`)}`;
  };

  /** The few talked with last, newest first; only those with a message (a friend never written to has no "last"). */
  const recentOf = <T,>(rows: T[], when: (row: T) => string): T[] =>
    rows.filter((r) => when(r) !== '').sort((a, b) => when(b).localeCompare(when(a))).slice(0, RECENT);

  /**
   * Friends or groups: "Gần đây", the five talked with last, then "Tất cả", everyone A to Z. With five or fewer there is no
   * "Gần đây" (it would repeat the list), and a search shows only what matches, A to Z.
   */
  const listing = () => {
    const friendsTab = tab === 'friends';
    const searching = query.trim() !== '';
    const total = friendsTab ? lists.friends.length : groups.length;
    const empty = total === 0 ? (friendsTab ? say.friends.noFriends : say.friends.noGroups) : say.friends.noMatch;
    const friends = lists.friends.filter((p) => matches(`${p.displayName} ${p.gomId}`, query));
    const groupRows = groups.filter((c) => matches(c.title ?? '', query)).sort((a, b) => byName(a.title ?? '', b.title ?? ''));
    const count = friendsTab ? friends.length : groupRows.length;
    const recent = searching || total <= RECENT ? [] : friendsTab
      ? recentOf(lists.friends, (p) => lastTalk.get(p.gomId) ?? '')
      : recentOf(groups, (c) => c.lastMessage?.createdAt ?? '');
    const all = friendsTab ? friendList(friends) : html`<ul class="list">${groupRows.map(groupRow)}</ul>`;
    return html`
      <div role="tabpanel" aria-labelledby=${`tab-${tab}`}>
        ${count === 0 ? html`<p class="hint" data-empty>${empty}</p>` : recent.length === 0 ? all : html`
          <h2 class="section" data-recent>${say.common.recent}</h2>
          <ul class="list">${friendsTab ? (recent as Person[]).map(friendRow) : (recent as Conversation[]).map(groupRow)}</ul>
          <h2 class="section" data-all>${say.friends.allCount(total)}</h2>
          ${all}`}
        ${friendsTab ? nothing : html`<a class="btn" href="/groups/new" data-new-group>${say.friends.newGroup}</a>`}
      </div>`;
  };

  const view = () => html`
    ${appBar({ title: say.friends.title, back: '/' })}
    <div class="page">
      ${errorBox(error)}
      ${user === null ? nothing : html`
        <details class="addbox" data-addbox>
          <summary class="btn secondary">${icons.users()}<span>${say.friends.addFriend}</span></summary>
          <form class="card" data-add @submit=${onAdd}>
            <label for="friend-id">${say.friends.byGomId}</label>
            <div class="input"><span class="prefix" aria-hidden="true">@</span>
              <input id="friend-id" name="friendId" type="text" autocomplete="off" autocapitalize="none" autocorrect="off" spellcheck="false"
                enterkeyhint="send" .value=${friendId} @input=${(e: Event) => { friendId = (e.target as HTMLInputElement).value; }}></div>
            <button class="btn" type="submit" ?disabled=${busy}>${say.friends.sendRequest}</button>
            <p class="hint" role="status" data-add-status>${status}</p>
          </form>
          <div class="card">
            <label for="my-link">${say.friends.myLink}</label>
            <div class="input"><input id="my-link" data-mylink readonly type="text" .value=${origin + '/add/' + user.gomId}>
              <button class="ib" type="button" data-copy aria-label=${say.friends.copyLink} @click=${copyLink}>${icons.copy()}</button></div>
            <p class="hint">${say.friends.myLinkNote}</p>
            <p class="hint" role="status" data-copy-status>${copied}</p>
          </div>
        </details>

        ${section(say.friends.incoming, lists.incoming.map((p) => html`
          <li class="li" data-incoming=${p.gomId}>${who(p, html` · ${formatTime(p.createdAt)}`)}
            ${button(say.friends.accept, 'accept', () => act(() => api.acceptFriend(p.gomId)), false)}
            ${button(say.friends.decline, 'decline', () => act(() => api.withdrawFriend(p.gomId)))}</li>`))}

        ${section(say.friends.outgoing, lists.outgoing.map((p) => html`
          <li class="li" data-outgoing=${p.gomId}>${who(p, html` · ${formatTime(p.createdAt)}`)}
            ${button(say.friends.withdraw, 'cancel', () => act(() => api.withdrawFriend(p.gomId)))}</li>`))}

        ${tabs()}
        <div class="searchfield input">${icons.search()}
          <input type="search" name="find" aria-label=${tab === 'friends' ? say.friends.findFriends : say.friends.findGroups} placeholder=${tab === 'friends' ? say.friends.findFriends : say.friends.findGroups}
            autocomplete="off" maxlength=${limits().nameChars} .value=${query} @input=${(e: Event) => { query = (e.target as HTMLInputElement).value; update(); }}></div>
        ${keyed(tab, listing())}

        ${tab === 'friends' ? section(say.friends.blocked, blocked.map((p) => html`
          <li class="li" data-blocked=${p.gomId}>${who(p)}
            ${button(say.friends.unblock, 'unblock', () => act(() => api.unblock(p.gomId)))}</li>`)) : nothing}`}
    </div>
    ${menuOf()}`;
  const update = () => { render(view(), root); placeMenu(root.querySelector<HTMLElement>('[data-row-actions]'), menu?.at); };

  async function reload(): Promise<void> {
    const [f, b, g] = await Promise.all([api.friends(), api.blocks(), api.conversations()]);
    if (f.ok) lists = f.data; else error = f;
    if (b.ok) blocked = b.data; else error = b;
    if (g.ok) {
      groups = g.data.filter((c) => c.kind === 'group');
      lastTalk = new Map(g.data.flatMap((c) => c.kind === 'direct' && c.peer?.gomId && c.lastMessage ? [[c.peer.gomId, c.lastMessage.createdAt] as const] : []));
    } else error = g;
    update();
  }

  async function act(run: () => Promise<{ ok: true } | Failure>): Promise<void> {
    const result = await run();
    if (!result.ok) { error = result; update(); return; }
    error = null; status = '';
    await reload();
  }

  async function message(gomId: string): Promise<void> {
    const result = await api.openDirect(gomId);
    if (!result.ok) { error = result; update(); return; }
    go(`/c/${result.data.id}`);
  }

  async function onAdd(event: Event): Promise<void> {
    event.preventDefault();
    const id = friendId.trim();
    if (busy || id === '') return;
    busy = true; error = null; status = ''; update();
    const result = await api.askFriend(id);
    busy = false;
    if (!result.ok) { error = result; update(); return; }
    status = result.data.status === 'friends' ? say.friends.alreadyFriends(id.toLowerCase()) : say.friends.requestSentTo(id.toLowerCase());
    friendId = '';
    await reload();
  }

  async function copyLink(): Promise<void> {
    if (user === null) return;
    try { await navigator.clipboard.writeText(origin + '/add/' + user.gomId); copied = say.friends.copied; } catch { copied = say.friends.copyFailed; }
    update();
  }

  update();
  void api.me().then(async (result) => {
    if (!result.ok) {
      if (result.error === 'unauthorized') go('/sign-in'); else { error = result; update(); }
      return;
    }
    user = result.data;
    await reload();
  });
}
