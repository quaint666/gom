import { html, render } from 'lit-html';
import type { Api, Failure, User } from '../api.js';
import * as icons from '../icons.js';
import { renderedScheme, toggleScheme } from '../theme.js';
import { appBar, errorBox } from '../ui.js';

/** The signed-in start page. There are no conversations yet, so it shows the quiet empty state. */
export function mountHome(root: HTMLElement, api: Api, go: (path: string) => void): void {
  let user: User | null = null;
  let error: Failure | null = null;

  const themeButton = () => html`
    <button class="ib" type="button" aria-label=${renderedScheme() === 'dark' ? 'Chuyển sang giao diện sáng' : 'Chuyển sang giao diện tối'}
      @click=${() => { toggleScheme(); update(); }}>${renderedScheme() === 'dark' ? icons.sun() : icons.moon()}</button>`;

  const view = () => html`
    ${appBar({ title: 'Gom', actions: html`${themeButton()}<a class="ib" href="/settings" aria-label="Cài đặt">${icons.gear()}</a>` })}
    <div class="page">
      ${errorBox(error)}
      ${user === null ? '' : html`
        <p class="lead">Xin chào, <b>${user.displayName}</b>.</p>
        <div class="empty">
          <div class="big-icon">${icons.logo()}</div>
          <h2>Chưa có cuộc trò chuyện nào</h2>
          <p class="lead">Tính năng nhắn tin sẽ sớm có ở đây.</p>
        </div>`}
    </div>`;
  const update = () => render(view(), root);

  update();
  void api.me().then((result) => {
    if (result.ok) { user = result.data; update(); }
    else if (result.error === 'unauthorized') go('/sign-in');
    else { error = result; update(); }
  });
}
