import { html, render } from 'lit-html';
import type { Api, Conversation, Failure, User } from '../api.js';
import * as icons from '../icons.js';
import { renderedScheme, toggleScheme } from '../theme.js';
import { appBar, errorBox } from '../ui.js';

/** The signed-in start page: your conversations, or a quiet empty state with a way to add friends. */
export function mountHome(root: HTMLElement, api: Api, go: (path: string) => void): void {
  let user: User | null = null;
  let chats: Conversation[] | null = null;
  let error: Failure | null = null;

  const themeButton = () => html`
    <button class="ib" type="button" aria-label=${renderedScheme() === 'dark' ? 'Chuyển sang giao diện sáng' : 'Chuyển sang giao diện tối'}
      @click=${() => { toggleScheme(); update(); }}>${renderedScheme() === 'dark' ? icons.sun() : icons.moon()}</button>`;

  const empty = () => html`
    <div class="empty">
      <div class="big-icon">${icons.logo()}</div>
      <h2>Chưa có cuộc trò chuyện nào</h2>
      <p class="lead">Hãy kết bạn rồi bắt đầu trò chuyện.</p>
      <a class="btn" href="/friends">Thêm bạn bè</a>
    </div>`;

  const row = (c: Conversation) => html`<li class="li" data-chat=${c.id}><div class="t"><b>${c.title ?? '…'}</b></div></li>`;

  const view = () => html`
    ${appBar({ title: 'Gom', actions: html`${themeButton()}
      <a class="ib" href="/friends" aria-label="Bạn bè">${icons.users()}</a>
      <a class="ib" href="/settings" aria-label="Cài đặt">${icons.gear()}</a>` })}
    <div class="page">
      ${errorBox(error)}
      ${user === null ? '' : html`
        <p class="lead">Xin chào, <b>${user.displayName}</b>.</p>
        ${chats === null ? '' : chats.length === 0 ? empty() : html`<ul class="list">${chats.map(row)}</ul>`}`}
    </div>`;
  const update = () => render(view(), root);

  update();
  void api.me().then((result) => {
    if (!result.ok) {
      if (result.error === 'unauthorized') go('/sign-in'); else { error = result; update(); }
      return;
    }
    user = result.data;
    update();
    void api.conversations().then((list) => {
      if (list.ok) chats = list.data;
      else if (list.error === 'unauthorized') go('/sign-in');
      else error = list;
      update();
    });
  });
}
