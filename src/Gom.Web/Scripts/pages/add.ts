import { html, nothing, render } from 'lit-html';
import type { Api, Failure } from '../api.js';
import { brand, errorBox } from '../ui.js';
import { say } from '../say.js';

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
        <div><h1>${say.account.befriendTitle(gomId)}</h1></div>
        ${errorBox(error)}
        ${done === '' ? html`<button class="btn" type="button" data-send ?disabled=${busy} @click=${send}>${say.account.sendFriendRequest}</button>` : nothing}
        ${done === '' ? nothing : html`<p class="lead" role="status">${done}</p>`}
        <a class="btn secondary" href="${done === '' ? '/' : '/friends'}">${done === '' ? say.account.toHome : say.account.seeFriends}</a>` : nothing}
    </div>`;
  const update = () => render(view(), root);

  async function send(): Promise<void> {
    if (busy) return;
    busy = true; error = null; update();
    const result = await api.askFriend(gomId);
    busy = false;
    if (!result.ok) { error = result; update(); return; }
    done = result.data.status === 'friends' ? say.friends.alreadyFriends(gomId) : say.friends.requestSentTo(gomId);
    update();
  }

  update();
  void api.me().then((result) => {
    if (result.ok) { ready = true; update(); }
    else if (result.error === 'unauthorized') go(`/sign-in?next=${encodeURIComponent('/add/' + gomId)}`);
    else { ready = true; error = result; update(); }
  });
}
