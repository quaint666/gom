import { html, nothing, render, type TemplateResult } from 'lit-html';
import type { Api, Conversation, FriendLists, Failure, Person, User } from '../api.js';
import * as icons from '../icons.js';
import { fold, matches } from '../search.js';
import { appBar, avatar, errorBox, formatTime } from '../ui.js';
import { limits } from '../limits.js';

/** How many rows of a long list show before "Xem thêm". */
const HANDFUL = 5;

const NONE: FriendLists = { friends: [], incoming: [], outgoing: [] };

/** Contacts: friends and groups in two tabs with a search, and adding by ID or by your own link, answering requests, unfriending, blocking. */
export function mountFriends(root: HTMLElement, api: Api, go: (path: string) => void, origin: string): void {
  let user: User | null = null;
  let lists: FriendLists = NONE;
  let blocked: Person[] = [];
  let groups: Conversation[] = [];
  let tab: 'friends' | 'groups' = 'friends';
  let query = '';
  let expanded = false;
  /** The friend whose "more" tray (unfriend, block) is open. */
  let tray: string | null = null;
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
      ? { q: `Xóa bạn với ${name}?`, hint: 'Hai người vẫn đọc được tin nhắn cũ, nhưng không nhắn thêm được cho đến khi kết bạn lại.', yes: 'Xóa bạn' }
      : { q: `Chặn ${name}?`, hint: `${name} sẽ không còn là bạn của bạn và không gửi được lời mời cho bạn. Bạn có thể bỏ chặn sau.`, yes: 'Chặn' };
    return html`<li class="card" role="group" aria-labelledby="sure-q" data-sure=${s.person.gomId}>
      <p id="sure-q"><b>${text.q}</b></p><p class="hint">${text.hint}</p>
      <button class="btn" type="button" data-confirm-sure @click=${() => { sure = null; void act(() => s.kind === 'unfriend' ? api.unfriend(s.person.gomId) : api.block(s.person.gomId)); }}>${text.yes}</button>
      <button class="btn secondary" type="button" data-cancel-sure @click=${() => { sure = null; update(); }}>Hủy</button>
    </li>`;
  };

  /** Up to a handful of rows; the rest behind "Xem thêm", unless something is being searched for. */
  const shown = <T,>(rows: T[]): { rows: T[]; hidden: number } =>
    query.trim() !== '' || expanded || rows.length <= HANDFUL ? { rows, hidden: 0 } : { rows: rows.slice(0, HANDFUL), hidden: rows.length - HANDFUL };

  const friendRow = (p: Person) => html`
    <li class="li person" data-friend=${p.gomId}>
      <button class="rowmain" type="button" data-action="message" @click=${() => void message(p.gomId)}>${avatar(p.displayName, false, false, p.avatar)}
        <span class="t"><b>${p.displayName}</b><small>@${p.gomId}</small></span></button>
      <button class="ib" type="button" data-more aria-label=${`Tùy chọn cho ${p.displayName}`} aria-expanded=${tray === p.gomId ? 'true' : 'false'}
        @click=${() => { tray = tray === p.gomId ? null : p.gomId; update(); }}>${icons.more()}</button>
      ${tray === p.gomId ? html`<div class="tray">
        ${button('Xóa bạn', 'unfriend', () => { tray = null; sure = { kind: 'unfriend', person: p }; update(); })}
        ${button('Chặn', 'block', () => { tray = null; sure = { kind: 'block', person: p }; update(); })}</div>` : nothing}
    </li>
    ${sure?.person.gomId === p.gomId ? question(sure) : nothing}`;

  const groupRow = (c: Conversation) => html`
    <li><a class="li person" href="/c/${c.id}" data-group=${c.id}>${avatar(c.title ?? '?')}
      <span class="t"><b>${c.title ?? '…'}</b><small>${c.memberCount ?? 0} thành viên</small></span></a></li>`;

  const tabs = () => {
    const mine = lists.friends.length;
    const tabButton = (id: 'friends' | 'groups', label: string, count: number) => html`
      <button class="tab" type="button" role="tab" id=${`tab-${id}`} data-tab=${id} aria-selected=${tab === id ? 'true' : 'false'}
        @click=${() => { tab = id; expanded = false; tray = null; update(); }}>${label} <span class="count">${count}</span></button>`;
    return html`<div class="tabs" role="tablist" aria-label="Danh bạ">${tabButton('friends', 'Bạn bè', mine)}${tabButton('groups', 'Nhóm', groups.length)}</div>`;
  };

  /** A name's letter for the A to Z headings: no accents, no capitals, and "Đ" is its own letter's cousin D. Anything else goes under "#". */
  const letterOf = (name: string): string => { const f = fold(name).charAt(0).toUpperCase(); return /[A-Z]/.test(f) ? f : '#'; };

  /** Friends by name. With everyone showing (not a handful), under a heading for each first letter. */
  const friendList = (rows: Person[], everyone: boolean) => {
    const sorted = [...rows].sort((a, b) => fold(a.displayName).localeCompare(fold(b.displayName), 'vi'));
    if (!everyone || sorted.length <= HANDFUL) return html`<ul class="list">${sorted.map(friendRow)}</ul>`;
    const letters = [...new Set(sorted.map((p) => letterOf(p.displayName)))].sort((a, b) => a === '#' ? 1 : b === '#' ? -1 : a.localeCompare(b));
    return html`${letters.map((l) => html`<h3 class="letter" data-letter=${l}>${l}</h3>
      <ul class="list">${sorted.filter((p) => letterOf(p.displayName) === l).map(friendRow)}</ul>`)}`;
  };

  const listing = () => {
    const rows = tab === 'friends'
      ? lists.friends.filter((p) => matches(`${p.displayName} ${p.gomId}`, query))
      : groups.filter((c) => matches(c.title ?? '', query));
    const total = tab === 'friends' ? lists.friends.length : groups.length;
    const part = shown(rows as unknown[]);
    const empty = total === 0
      ? (tab === 'friends' ? 'Chưa có bạn bè nào. Hãy gửi lời mời ở trên.' : 'Bạn chưa ở trong nhóm nào.')
      : 'Không tìm thấy ai khớp với từ khóa.';
    return html`
      <div role="tabpanel" aria-labelledby=${`tab-${tab}`}>
        ${rows.length === 0 ? html`<p class="hint" data-empty>${empty}</p>` : html`
          ${tab === 'friends' ? friendList(part.rows as Person[], part.hidden === 0) : html`<ul class="list">${(part.rows as Conversation[]).map(groupRow)}</ul>`}
          ${part.hidden > 0 ? html`<button class="btn secondary" type="button" data-more-rows @click=${() => { expanded = true; update(); }}>Xem thêm (${part.hidden})</button>` : nothing}`}
        ${tab === 'groups' ? html`<a class="btn" href="/groups/new" data-new-group>Nhóm mới</a>` : nothing}
      </div>`;
  };

  const view = () => html`
    ${appBar({ title: 'Danh bạ', back: '/' })}
    <div class="page">
      ${errorBox(error)}
      ${user === null ? nothing : html`
        <details class="addbox" data-addbox>
          <summary class="btn secondary">${icons.users()}<span>Thêm bạn</span></summary>
          <form class="card" data-add @submit=${onAdd}>
            <label for="friend-id">Thêm bạn bằng Gom ID</label>
            <div class="input"><span class="prefix" aria-hidden="true">@</span>
              <input id="friend-id" name="friendId" type="text" autocomplete="off" autocapitalize="none" autocorrect="off" spellcheck="false"
                enterkeyhint="send" .value=${friendId} @input=${(e: Event) => { friendId = (e.target as HTMLInputElement).value; }}></div>
            <button class="btn" type="submit" ?disabled=${busy}>Gửi lời mời</button>
            <p class="hint" role="status" data-add-status>${status}</p>
          </form>
          <div class="card">
            <label for="my-link">Liên kết kết bạn của bạn</label>
            <div class="input"><input id="my-link" data-mylink readonly type="text" .value=${origin + '/add/' + user.gomId}>
              <button class="ib" type="button" data-copy aria-label="Sao chép liên kết" @click=${copyLink}>${icons.copy()}</button></div>
            <p class="hint">Ai có liên kết này và đã đăng nhập có thể gửi lời mời kết bạn cho bạn.</p>
            <p class="hint" role="status" data-copy-status>${copied}</p>
          </div>
        </details>

        ${section('Lời mời nhận được', lists.incoming.map((p) => html`
          <li class="li" data-incoming=${p.gomId}>${who(p, html` · ${formatTime(p.createdAt)}`)}
            ${button('Chấp nhận', 'accept', () => act(() => api.acceptFriend(p.gomId)), false)}
            ${button('Từ chối', 'decline', () => act(() => api.withdrawFriend(p.gomId)))}</li>`))}

        ${section('Lời mời đã gửi', lists.outgoing.map((p) => html`
          <li class="li" data-outgoing=${p.gomId}>${who(p, html` · ${formatTime(p.createdAt)}`)}
            ${button('Hủy lời mời', 'cancel', () => act(() => api.withdrawFriend(p.gomId)))}</li>`))}

        ${tabs()}
        <div class="searchfield input">${icons.search()}
          <input type="search" name="find" aria-label=${tab === 'friends' ? 'Tìm bạn bè' : 'Tìm nhóm'} placeholder=${tab === 'friends' ? 'Tìm bạn bè' : 'Tìm nhóm'}
            autocomplete="off" maxlength=${limits().nameChars} .value=${query} @input=${(e: Event) => { query = (e.target as HTMLInputElement).value; expanded = false; update(); }}></div>
        ${listing()}

        ${tab === 'friends' ? section('Đã chặn', blocked.map((p) => html`
          <li class="li" data-blocked=${p.gomId}>${who(p)}
            ${button('Bỏ chặn', 'unblock', () => act(() => api.unblock(p.gomId)))}</li>`)) : nothing}`}
    </div>`;
  const update = () => render(view(), root);

  async function reload(): Promise<void> {
    const [f, b, g] = await Promise.all([api.friends(), api.blocks(), api.conversations()]);
    if (f.ok) lists = f.data; else error = f;
    if (b.ok) blocked = b.data; else error = b;
    if (g.ok) groups = g.data.filter((c) => c.kind === 'group'); else error = g;
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
    status = result.data.status === 'friends' ? `Bạn và @${id.toLowerCase()} đã là bạn bè.` : `Đã gửi lời mời đến @${id.toLowerCase()}.`;
    friendId = '';
    await reload();
  }

  async function copyLink(): Promise<void> {
    if (user === null) return;
    try { await navigator.clipboard.writeText(origin + '/add/' + user.gomId); copied = 'Đã sao chép'; } catch { copied = 'Không sao chép được. Hãy chọn và chép liên kết bằng tay.'; }
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
