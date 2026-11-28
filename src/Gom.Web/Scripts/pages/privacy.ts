import { html, nothing, render } from 'lit-html';
import type { Api, Failure } from '../api.js';
import * as icons from '../icons.js';
import { limits } from '../limits.js';
import { brand, errorBox } from '../ui.js';
import { say } from '../say.js';

/** When the policy's words last changed: shown at the top, in the page's language. Change it with the words. */
export const POLICY_DATE = '2026-11-26';

/**
 * The privacy policy, in the page's language (a signed-in person's choice as this device knows it, else the browser's), and a form
 * to write to the operator (Play wants a way to ask about privacy). Public: no sign-in needed. Who runs this Gom comes from the
 * server's configuration (`data-operator`, Site:Operator).
 */
export function mountPrivacy(root: HTMLElement, operator: string, api: Pick<Api, 'contact'>, source = ''): void {
  const date = new Intl.DateTimeFormat(say.locale, { day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' }).format(new Date(`${POLICY_DATE}T00:00:00Z`));
  const who = operator.trim() === '' ? say.privacy.operatorFallback : operator.trim();
  let body = '';
  let replyTo = '';
  let trap = '';
  let busy = false;
  let sent = false;
  let error: Failure | null = null;

  const form = () => sent
    ? html`<p class="note" role="status" data-contact-sent>${say.privacy.contactSent}</p>`
    : html`<form @submit=${onSubmit} data-contact-form>
        ${errorBox(error)}
        <div class="field">
          <label for="contact-body">${say.privacy.contactBody}</label>
          <div class="input"><textarea id="contact-body" name="body" rows="5" required maxlength=${limits().contactChars}
            .value=${body} @input=${(e: Event) => { body = (e.target as HTMLTextAreaElement).value; }}></textarea></div>
        </div>
        <div class="field">
          <label for="contact-reply">${say.privacy.contactReply}</label>
          <div class="input"><input id="contact-reply" name="replyTo" type="text" autocomplete="email" maxlength=${limits().contactReplyChars}
            .value=${replyTo} @input=${(e: Event) => { replyTo = (e.target as HTMLInputElement).value; }}></div>
          <p class="hint">${say.privacy.contactReplyHint}</p>
        </div>
        <div class="sr-only" aria-hidden="true"><label for="contact-website">${say.privacy.contactTrap}</label>
          <input id="contact-website" name="website" type="text" tabindex="-1" autocomplete="off"
            .value=${trap} @input=${(e: Event) => { trap = (e.target as HTMLInputElement).value; }}></div>
        <button class="btn" type="submit" ?disabled=${busy}>${say.privacy.contactSend}</button>
      </form>`;

  const view = () => html`
    <article class="page policy" data-privacy>
      <a class="btn secondary small home-btn" href="/" data-home-top>${icons.back()}${say.privacy.home}</a>
      <a class="policy-home" href="/">${brand()}</a>
      <h1>${say.privacy.title}</h1>
      <p class="hint" data-policy-date>${say.privacy.updated(date)}</p>
      <p data-policy-intro>${say.privacy.intro(who)}</p>
      ${say.privacy.sections.map((s) => html`
        <section><h2>${s.heading}</h2><ul>${s.items.map((item) => html`<li>${item}</li>`)}</ul></section>`)}
      ${source === '' ? nothing : html`<section data-source><h2>${say.privacy.sourceHeading}</h2><p>${say.privacy.sourceLead} <a href=${source} target="_blank" rel="noopener" data-source-link>${source}</a></p></section>`}
      <section data-contact><h2>${say.privacy.contactHeading}</h2><p>${say.privacy.contactLead(limits().contactKeepDays)}</p>${form()}</section>
      <a class="btn secondary home-btn" href="/" data-home>${icons.back()}${say.privacy.home}</a>
    </article>`;
  const update = () => render(view(), root);

  async function onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    if (busy) return;
    busy = true; error = null; update();
    const result = await api.contact(body, replyTo, trap);
    busy = false;
    if (result.ok) { sent = true; body = ''; replyTo = ''; } else error = result;
    update();
  }

  update();
}
