import { html, nothing, render } from 'lit-html';
import { live } from 'lit-html/directives/live.js';
import type { Api, Failure, Person } from '../api.js';
import { appBar, errorBox } from '../ui.js';

/** Make a group: a name, and some of your friends. */
export function mountNewGroup(root: HTMLElement, api: Api, go: (path: string) => void): void {
  let friends: Person[] | null = null;
  const picked = new Set<string>();
  let title = '';
  let busy = false;
  let error: Failure | null = null;

  const view = () => html`
    ${appBar({ title: 'Nhóm mới', back: '/' })}
    <div class="page">
      ${errorBox(error)}
      ${friends === null ? nothing : friends.length === 0 ? html`
        <p class="lead">Bạn chưa có bạn bè nào để lập nhóm. Hãy kết bạn trước.</p>
        <a class="btn" href="/friends">Thêm bạn bè</a>` : html`
        <form @submit=${onSubmit}>
          <div class="field">
            <label for="group-title">Tên nhóm</label>
            <div class="input"><input id="group-title" name="title" type="text" maxlength="60" autocomplete="off" enterkeyhint="done"
              .value=${live(title)} @input=${(e: Event) => { title = (e.target as HTMLInputElement).value; }}></div>
          </div>
          <fieldset class="picks">
            <legend class="section">Chọn thành viên</legend>
            ${friends.map((f, i) => html`
              <div class="pick"><input type="checkbox" id="pick-${i}" value=${f.gomId} .checked=${live(picked.has(f.gomId))}
                @change=${(e: Event) => toggle(f.gomId, (e.target as HTMLInputElement).checked)}>
                <label for="pick-${i}"><b>${f.displayName}</b> <small>@${f.gomId}</small></label></div>`)}
          </fieldset>
          <button class="btn" type="submit" ?disabled=${busy}>Tạo nhóm</button>
        </form>`}
    </div>`;
  const update = () => render(view(), root);

  function toggle(gomId: string, on: boolean): void { if (on) picked.add(gomId); else picked.delete(gomId); }

  async function onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    if (busy) return;
    if (title.trim() === '') { error = { ok: false, status: 0, error: 'invalid_title' }; update(); return; }
    if (picked.size === 0) { error = { ok: false, status: 0, error: 'no_members' }; update(); return; }
    busy = true; error = null; update();
    const result = await api.createGroup(title.trim(), [...picked]);
    busy = false;
    if (result.ok) { go(`/c/${result.data.id}`); return; }
    error = result;
    update();
  }

  update();
  void api.me().then(async (me) => {
    if (!me.ok) { if (me.error === 'unauthorized') go('/sign-in'); else { error = me; update(); } return; }
    const result = await api.friends();
    if (result.ok) friends = result.data.friends; else error = result;
    update();
  });
}
