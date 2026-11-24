import { html, nothing, render } from 'lit-html';
import type { Api, Failure } from '../api.js';
import { brand, errorBox } from '../ui.js';

/** Opened from someone's add-me link. Nothing is sent until the button is pressed: a link must never change anything. */
export function mountAdd(root: HTMLElement, api: Api, go: (path: string) => void, gomId: string): void {
  let ready = false;
  let done = '';
  let busy = false;
  let error: Failure | null = null;

  const view = () => html`
    <div class="page center">
      ${brand()}
      ${ready ? html`
        <div><h1>Kết bạn với @${gomId}</h1></div>
        ${errorBox(error)}
        ${done === '' ? html`<button class="btn" type="button" data-send ?disabled=${busy} @click=${send}>Gửi lời mời kết bạn</button>` : nothing}
        ${done === '' ? nothing : html`<p class="lead" role="status">${done}</p>`}
        <a class="btn secondary" href="${done === '' ? '/' : '/friends'}">${done === '' ? 'Về trang chính' : 'Xem bạn bè'}</a>` : nothing}
    </div>`;
  const update = () => render(view(), root);

  async function send(): Promise<void> {
    if (busy) return;
    busy = true; error = null; update();
    const result = await api.askFriend(gomId);
    busy = false;
    if (!result.ok) { error = result; update(); return; }
    done = result.data.status === 'friends' ? `Bạn và @${gomId} đã là bạn bè.` : `Đã gửi lời mời đến @${gomId}.`;
    update();
  }

  update();
  void api.me().then((result) => {
    if (result.ok) { ready = true; update(); }
    else if (result.error === 'unauthorized') go(`/sign-in?next=${encodeURIComponent('/add/' + gomId)}`);
    else { ready = true; error = result; update(); }
  });
}
