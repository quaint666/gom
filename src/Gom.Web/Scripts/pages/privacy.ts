import { html, render } from 'lit-html';
import { brand } from '../ui.js';
import { say } from '../say.js';

/** When the policy's words last changed: shown at the top, in the page's language. Change it with the words. */
export const POLICY_DATE = '2026-11-26';

/**
 * The privacy policy, in the page's language (a signed-in person's choice as this device knows it, else the browser's). Public: it
 * asks the server nothing. Who runs this Gom comes from the server's configuration (`data-operator`, Site:Operator).
 */
export function mountPrivacy(root: HTMLElement, operator: string): void {
  const date = new Intl.DateTimeFormat(say.locale, { day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' }).format(new Date(`${POLICY_DATE}T00:00:00Z`));
  const who = operator.trim() === '' ? say.privacy.operatorFallback : operator.trim();
  render(html`
    <article class="page policy" data-privacy>
      <a class="policy-home" href="/">${brand()}</a>
      <h1>${say.privacy.title}</h1>
      <p class="hint" data-policy-date>${say.privacy.updated(date)}</p>
      <p data-policy-intro>${say.privacy.intro(who)}</p>
      ${say.privacy.sections.map((s) => html`
        <section><h2>${s.heading}</h2><ul>${s.items.map((item) => html`<li>${item}</li>`)}</ul></section>`)}
      <p class="foot"><a href="/">${say.privacy.home}</a></p>
    </article>`, root);
}
