import { html, nothing, render } from 'lit-html';
import type { Api, Failure } from '../api.js';
import * as icons from '../icons.js';
import { brand, errorBox } from '../ui.js';
import { say } from '../say.js';

/**
 * Shown once, right after an account is made (through an invite, or when the owner approves a request): what Gom is, and the way in.
 * `group`: the group an invite added them to (?group=); without it, their only group if they have exactly one (an approval into a
 * group, where the waiting page cannot know which).
 */
export function mountWelcome(root: HTMLElement, api: Api, go: (path: string) => void, group: string | null, source = ''): void {
  let name = '';
  let into: { id: string; title: string } | null = null;
  let error: Failure | null = null;

  const point = (icon: unknown, title: string, text: string, attr: string) => html`
    <li class="welcome-point" data-welcome=${attr}><span class="lead-i">${icon}</span><span class="t"><b>${title}</b><small>${text}</small></span></li>`;

  const view = () => html`
    <div class="page welcome">
      ${brand()}
      <div><h1>${name === '' ? say.welcome.titleNoName : say.welcome.title(name)}</h1><p class="lead">${say.welcome.lead}</p></div>
      ${errorBox(error)}
      <ul class="welcome-points">
        ${point(icons.lock(), say.welcome.inviteOnly, say.welcome.inviteOnlyNote, 'invite-only')}
        ${point(icons.disk(), say.welcome.ownServer, say.welcome.ownServerNote, 'own-server')}
        ${into === null ? nothing : point(icons.users(), say.welcome.inGroup(into.title), say.welcome.inGroupNote, 'group')}
      </ul>
      ${into === null
        ? html`<a class="btn" href="/" data-welcome-start @click=${(e: MouseEvent) => { e.preventDefault(); go('/'); }}>${say.welcome.start}</a>`
        : html`<a class="btn" href=${`/c/${into.id}`} data-welcome-group @click=${(e: MouseEvent) => { e.preventDefault(); go(`/c/${into!.id}`); }}>${say.welcome.openGroup(into.title)}</a>`}
      <a class="btn secondary" href="/settings/profile" data-welcome-picture @click=${(e: MouseEvent) => { e.preventDefault(); go('/settings/profile'); }}>${say.welcome.picture}</a>
      ${source === '' ? nothing : html`<p class="foot">${say.welcome.source} <a href=${source} target="_blank" rel="noopener" data-source-link>GitHub</a></p>`}
    </div>`;
  const update = () => render(view(), root);

  update();
  void (async () => {
    const me = await api.me();
    if (!me.ok) { if (me.error === 'unauthorized') { go('/sign-in'); return; } error = me; update(); return; }
    name = me.data.displayName;
    update();
    const list = await api.conversations();
    if (!list.ok) return;
    const groups = list.data.filter((c) => c.kind === 'group');
    const found = group === null ? (groups.length === 1 ? groups[0] : undefined) : groups.find((c) => c.id === group);
    if (found !== undefined) { into = { id: found.id, title: found.title ?? '…' }; update(); }
  })();
}
