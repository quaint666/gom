import { html, nothing, render } from 'lit-html';
import { live } from 'lit-html/directives/live.js';
import type { Api, Failure, Person } from '../api.js';
import * as icons from '../icons.js';
import { matches } from '../search.js';
import { appBar, avatar, errorBox } from '../ui.js';
import { limits } from '../limits.js';
import { say } from '../say.js';

/** Make a group: a name, and some of your friends. `withFriend` (a Gom ID, from "make a group with this friend") starts ticked. */
export function mountNewGroup(root: HTMLElement, api: Api, go: (path: string) => void, withFriend: string | null = null): void {
  let friends: Person[] | null = null;
  const picked = new Set<string>();
  let title = '';
  let query = '';
  let busy = false;
  let error: Failure | null = null;

  const view = () => html`
    ${appBar({ title: say.newGroup.title, back: '/' })}
    <div class="page">
      ${errorBox(error)}
      ${friends === null ? nothing : friends.length === 0 ? html`
        <p class="lead">${say.newGroup.noFriends}</p>
        <a class="btn" href="/friends">${say.common.addFriends}</a>` : html`
        <form @submit=${onSubmit}>
          <div class="field">
            <label for="group-title">${say.group.name}</label>
            <div class="input"><input id="group-title" name="title" type="text" maxlength=${limits().nameChars} autocomplete="off" enterkeyhint="done"
              .value=${live(title)} @input=${(e: Event) => { title = (e.target as HTMLInputElement).value; }}></div>
          </div>
          <fieldset class="picks">
            <legend class="section">${say.newGroup.pickMembers}${picked.size > 0 ? html` · <span data-picked>${picked.size}</span>` : nothing}</legend>
            <div class="searchfield input">${icons.search()}
              <input type="search" name="find" aria-label=${say.friends.findFriends} placeholder=${say.friends.findFriends} autocomplete="off" maxlength=${limits().nameChars}
                .value=${live(query)} @input=${(e: Event) => { query = (e.target as HTMLInputElement).value; update(); }}></div>
            ${friends.map((f, i) => !matches(`${f.displayName} ${f.gomId}`, query) ? nothing : html`
              <div class="pick"><input type="checkbox" id="pick-${i}" value=${f.gomId} .checked=${live(picked.has(f.gomId))}
                @change=${(e: Event) => { toggle(f.gomId, (e.target as HTMLInputElement).checked); update(); }}>
                <label for="pick-${i}">${avatar(f.displayName, true, false, f.avatar)}<span><b>${f.displayName}</b> <small>@${f.gomId}</small></span></label></div>`)}
            ${friends.every((f) => !matches(`${f.displayName} ${f.gomId}`, query)) ? html`<p class="hint" data-no-match>${say.friends.noMatch}</p>` : nothing}
          </fieldset>
          <button class="btn" type="submit" ?disabled=${busy}>${say.newGroup.create}</button>
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
    if (result.ok) {
      friends = result.data.friends;
      if (withFriend !== null && friends.some((f) => f.gomId === withFriend)) picked.add(withFriend);
    } else error = result;
    update();
  });
}
