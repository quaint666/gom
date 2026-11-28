import { html, render } from 'lit-html';
import * as icons from '../icons.js';
import { brand } from '../ui.js';
import { say } from '../say.js';

/** The device this page is open on: inside the Android app, on an iPhone or iPad, or another browser. */
export type HelpDevice = 'android' | 'iphone' | 'web';

/** Which part comes first: the app's own, an Apple device's, else the browser's (iPadOS says it is a Mac, but has a touch screen). */
export function helpDevice(inApp: boolean, nav: Pick<Navigator, 'userAgent' | 'platform' | 'maxTouchPoints'> = navigator): HelpDevice {
  if (inApp) return 'android';
  return /iPhone|iPad|iPod/.test(nav.userAgent) || (nav.platform === 'MacIntel' && nav.maxTouchPoints > 1) ? 'iphone' : 'web';
}

/**
 * How to set Gom up so messages and calls arrive: notifications, the microphone, and on Android the battery. Public: no sign-in
 * needed, so the pages before signing in can link to it. The part for this device comes first and says so; the others follow.
 */
export function mountHelp(root: HTMLElement, device: HelpDevice): void {
  const order: HelpDevice[] = [device, ...(['web', 'android', 'iphone'] as const).filter((d) => d !== device)];
  const part = (d: HelpDevice) => {
    const s = say.help[d];
    return html`
      <section data-help-part=${d}>
        <h2>${s.heading}${d === device ? html` <small class="hint" data-this-device>· ${say.help.thisDevice}</small>` : ''}</h2>
        <ol>${s.items.map((item) => html`<li>${item}</li>`)}</ol>
      </section>`;
  };
  render(html`
    <article class="page policy" data-help>
      <a class="btn secondary small home-btn" href="/" data-home-top>${icons.back()}${say.help.home}</a>
      <a class="policy-home" href="/">${brand()}</a>
      <h1>${say.help.title}</h1>
      <p>${say.help.lead}</p>
      ${order.map(part)}
      <a class="btn secondary home-btn" href="/" data-home>${icons.back()}${say.help.home}</a>
    </article>`, root);
}
