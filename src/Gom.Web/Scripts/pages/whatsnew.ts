import { html, render } from 'lit-html';
import * as icons from '../icons.js';
import { brand } from '../ui.js';
import { say } from '../say.js';

/** The newest release the page lists: what Settings' row says is the latest. */
export const latestRelease = (): string => say.whatsNew.releases[0]!.version;

/**
 * What changed in each release of Gom, newest first, from 1.0.0. The newest is open and the others are folded, so a phone does
 * not scroll through them all; a tap opens one (several may be open). Public, like the setup guide. The lines are in the strings,
 * one entry per release, written for the people who use Gom (docs/releases.md has the developer's notes).
 */
export function mountWhatsNew(root: HTMLElement): void {
  render(html`
    <article class="page policy" data-whats-new>
      <a class="btn secondary small home-btn" href="/settings" data-home-top>${icons.back()}${say.whatsNew.back}</a>
      <a class="policy-home" href="/">${brand()}</a>
      <h1>${say.whatsNew.title}</h1>
      <p>${say.whatsNew.lead}</p>
      ${say.whatsNew.releases.map((r, i) => html`
        <details class="rel" data-release=${r.version} ?open=${i === 0}>
          <summary>${say.whatsNew.version(r.version)}</summary>
          <ul>${r.items.map((item) => html`<li>${item}</li>`)}</ul>
        </details>`)}
    </article>`, root);
}
