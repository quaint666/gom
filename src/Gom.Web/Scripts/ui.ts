import { html, nothing, type TemplateResult } from 'lit-html';
import type { Failure } from './api.js';
import * as icons from './icons.js';
import { avatarTone, initials } from './chat/model.js';
import { errorText } from './text.js';

export const brand = (): TemplateResult => html`<div class="logo">${icons.logo()}<span>Gom</span></div>`;

/** A sentence about what went wrong, with an icon (never colour alone). Announced by screen readers when it appears. */
export const errorBox = (failure: Failure | null): TemplateResult | typeof nothing =>
  failure === null ? nothing : html`<div class="alert" role="alert">${icons.alert()}<div>${errorText(failure)}</div></div>`;

export interface PasswordFieldOptions {
  label: string;
  /** The field's name and id; two password fields on one page need two. */
  name?: string;
  autocomplete: 'current-password' | 'new-password';
  value: string;
  shown: boolean;
  hint?: string;
  onInput: (value: string) => void;
  onToggle: () => void;
}

/** One password field (never asked twice) with a show/hide button. Pasting is allowed. */
export const passwordField = (o: PasswordFieldOptions): TemplateResult => html`
  <div class="field">
    <label for=${o.name ?? 'password'}>${o.label}</label>
    <div class="input">
      <input id=${o.name ?? 'password'} name=${o.name ?? 'password'} type=${o.shown ? 'text' : 'password'} autocomplete=${o.autocomplete} required
        enterkeyhint="done" .value=${o.value} @input=${(e: Event) => o.onInput((e.target as HTMLInputElement).value)}>
      <button type="button" class="ib" aria-pressed=${o.shown ? 'true' : 'false'} aria-label="Hiện mật khẩu" @click=${o.onToggle}>${icons.eye()}</button>
    </div>
    ${o.hint ? html`<p class="hint">${o.hint}</p>` : nothing}
  </div>`;

export interface IdFieldOptions { value: string; enterkeyhint: 'next' | 'done'; onInput: (value: string) => void; hint?: string }

/** The Gom ID: an identifier, not prose, so no capital letters, autocorrect or spellcheck. */
export const idField = (o: IdFieldOptions): TemplateResult => html`
  <div class="field">
    <label for="gom-id">Gom ID</label>
    <div class="input"><span class="prefix" aria-hidden="true">@</span>
      <input id="gom-id" name="gomId" type="text" autocomplete="username" autocapitalize="none" autocorrect="off" spellcheck="false"
        required enterkeyhint=${o.enterkeyhint} .value=${o.value} @input=${(e: Event) => o.onInput((e.target as HTMLInputElement).value)}>
    </div>
    ${o.hint ? html`<p class="hint">${o.hint}</p>` : nothing}
  </div>`;

/** The "this link no longer works" screen, for a dead invite or reset link. */
export const deadLink = (title: string, body: string): TemplateResult => html`
  <div class="page center">
    ${brand()}
    <div class="big-icon">${icons.clock()}</div>
    <div><h1>${title}</h1><p class="lead">${body}</p></div>
    <a class="btn secondary" href="/sign-in">Đăng nhập</a>
  </div>`;

export interface BarOptions { title: string; back?: string; actions?: TemplateResult | typeof nothing }

export const appBar = (o: BarOptions): TemplateResult => html`
  <header class="bar">
    ${o.back ? html`<a class="ib" href=${o.back} aria-label="Quay lại">${icons.back()}</a>` : nothing}
    <h1>${o.title}</h1>
    ${o.actions ?? nothing}
  </header>`;

const dateFormat = new Intl.DateTimeFormat('vi-VN', { dateStyle: 'medium', timeStyle: 'short' });
export const formatTime = (iso: string): string => dateFormat.format(new Date(iso));

/** Where a profile picture is fetched from, by its id. */
export const avatarUrl = (picture: string): string => `/api/avatars/${encodeURIComponent(picture)}`;

/** A round badge: the person's picture when they have one, else the one or two letters of their name. */
export const avatar = (name: string, small = false, big = false, picture?: string | null): TemplateResult =>
  html`<div class="av a${avatarTone(name)} ${small ? 'sm' : ''} ${big ? 'xl' : ''} ${picture ? 'pic' : ''}" aria-hidden="true">${picture
    ? html`<img src=${avatarUrl(picture)} alt="" loading="lazy" decoding="async">` : initials(name)}</div>`;
