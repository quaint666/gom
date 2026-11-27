import { html, type TemplateResult } from 'lit-html';
import { html as staticHtml, unsafeStatic } from 'lit-html/static.js';
import { say } from './say.js';

/**
 * A row's menu of actions, the one a conversation's "..." opens (`.menu` in gom.css): beside the button on a computer, a sheet from
 * the bottom on a phone. The chat list, the owner's page and Contacts all use it, so every "..." in Gom behaves alike.
 */

/** Where a menu opens for a click on a row's "...": under the button, its right edge at the button's (the menu is 236 px wide). */
export function menuPoint(e: MouseEvent): { x: number; y: number } {
  const r = (e.currentTarget as HTMLElement | null)?.getBoundingClientRect();
  return r === undefined ? { x: e.clientX, y: e.clientY } : { x: r.right - 236, y: r.bottom };
}

/**
 * Puts a drawn menu where it was asked for, moved up as far as it must to stay on the screen. Set through the style object: the
 * page's content security policy refuses a style attribute, but allows this.
 */
export function placeMenu(pop: HTMLElement | null, at: { x: number; y: number } | undefined): void {
  if (pop === null || at === undefined) return;
  const room = innerHeight - pop.offsetHeight - 8;
  pop.style.setProperty('--x', `${Math.round(at.x)}px`);
  pop.style.setProperty('--y', `${Math.round(Math.max(8, Math.min(at.y, room)))}px`);
}

/** One line of a menu: an icon and a word, marked with `data-<attr>` for tests and styles. */
export const menuItem = (attr: string, icon: TemplateResult, label: string, run: () => void, danger = false): TemplateResult =>
  staticHtml`<button class="mi ${danger ? 'danger' : ''}" type="button" role="menuitem" data-${unsafeStatic(attr)} @click=${run}>${icon}<span>${label}</span></button>`;

/** A whole menu with its shade: a tap outside, Escape or "Đóng" (shown on a phone) closes it. */
export const menuView = (label: string, items: TemplateResult[], close: () => void): TemplateResult => html`
  <div class="scrim" data-scrim @click=${close}></div>
  <div class="menu" role="menu" aria-label=${label} data-row-actions @keydown=${(e: KeyboardEvent) => { if (e.key === 'Escape') close(); }}>
    <div class="items-grid">${items}</div>
    <button class="btn secondary small close-menu" type="button" data-close-menu @click=${close}>${say.common.close}</button>
  </div>`;
