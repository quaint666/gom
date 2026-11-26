import { html, nothing, render, type TemplateResult } from 'lit-html';
import { live } from 'lit-html/directives/live.js';
import { ref } from 'lit-html/directives/ref.js';
import type { Api, Failure, Person, PushLevel, PushState, SessionInfo, User } from '../api.js';
import * as icons from '../icons.js';
import { mountOwner } from './owner.js';
import { mountStorage } from './storage.js';
import { PALETTES, PATTERNS, applyBlack, applyPalette, chosenScheme, chooseScheme, rememberPattern, storedBlack, storedPalette, storedPattern, type Choice, type Palette, type Pattern } from '../theme.js';
import type { Installer } from '../install.js';
import { NO_PUSH_SERVICE, type Pusher } from '../push.js';
import { appBar, avatar, errorBox, formatTime, passwordField } from '../ui.js';
import { errorText, passwordHint } from '../text.js';
import { LANGUAGES, language, rememberLanguage, say, type Language } from '../say.js';
import { limits } from '../limits.js';

/** The choices for the colour scheme, in the order shown. */
const SCHEMES: ReadonlyArray<readonly [Choice, string]> = [['system', say.settings.schemeSystem], ['light', say.settings.schemeLight], ['dark', say.settings.schemeDark]];

/**
 * The parts of Settings, each at /settings/{code}: a phone shows the menu, then one part with a way back; a wide screen shows the menu
 * beside the open part (Hồ sơ when none is). The owner's two are only for the owner. The server lists the same codes (Pages.cs).
 */
export const SECTIONS = ['profile', 'notifications', 'appearance', 'privacy', 'account', 'owner', 'storage'] as const;
export type Section = typeof SECTIONS[number];
const OWNER_ONLY: readonly Section[] = ['owner', 'storage'];

/** The address bar, so a part has an address of its own and the back button leaves it. */
export interface SettingsNav { path(): string; push(path: string): void; replace(path: string): void; onPop(callback: () => void): void }

/** The part an address names, or null for the menu (an unknown name is the menu too). */
export function sectionOf(path: string): Section | null {
  const code = /^\/settings\/([a-z]+)\/?$/.exec(path)?.[1];
  return (SECTIONS as readonly string[]).includes(code ?? '') ? code as Section : null;
}

export function mountSettings(root: HTMLElement, api: Api, go: (path: string) => void, installer?: Installer, pusher?: Pusher, nav?: SettingsNav): void {
  let section: Section | null = sectionOf(nav?.path() ?? '/settings');
  let user: User | null = null;
  let blocked: Person[] = [];
  /** The name row is open for changing the name. */
  let nameOpen = false;
  let linkStatus = '';
  /** The owner's pages mounted in their part, so opening it again does not load it twice. */
  const embedded = new WeakSet<Element>();
  let sessions: SessionInfo[] = [];
  let error: Failure | null = null;
  let nickname = '';
  let nicknameStatus = '';
  let current = '';
  let next = '';
  let shown = false;
  let passwordStatus = '';
  let receipts = true;
  let receiptsStatus = '';
  let busy = false;
  let scheme: Choice = chosenScheme();
  let palette: Palette = storedPalette();
  let pattern: Pattern = storedPattern();
  let black = storedBlack();
  let installStatus = '';
  /** A picture chosen for the profile, not saved yet: the file, and what the preview shows (read as a data: address, which the page's rules allow). */
  let picked: { file: File; preview: string } | null = null;
  let pictureStatus = '';
  /** Notifications on this server (null: off, or not known yet), and on this device. */
  let push: PushState | null = null;
  let pushStatus = '';
  let pushBusy = false;
  /** What is typed to confirm deleting the account: the person's own Gom ID. */
  let confirmDelete = '';

  const row = (s: SessionInfo) => html`
    <li class="li" data-session=${s.id}>
      <div class="t">
        <b>${s.deviceLabel}${s.current ? html`<span class="tag">${say.settings.thisDevice}</span>` : nothing}</b>
        <small>${say.settings.lastSeen(formatTime(s.lastSeenAt))}</small>
      </div>
      ${s.current ? nothing : html`<button class="btn secondary small" type="button" @click=${() => endSession(s.id)}>${say.settings.signOutDevice}</button>`}
    </li>`;

  /** The part shown in the pane: the open one, or Hồ sơ beside the menu on a wide screen. */
  const openPart = (): Section => section ?? 'profile';
  const title = (s: Section): string => ({
    profile: say.settings.profile, notifications: say.settings.notifications, appearance: say.settings.appearanceAndLanguage,
    privacy: say.settings.privacy, account: say.settings.account, owner: say.common.ownerPage, storage: say.settings.storage,
  })[s];

  /** Opens a part in place (no new page), with its own address; a click with a modifier key opens the link as a browser does. */
  function open(e: MouseEvent, s: Section): void {
    if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
    e.preventDefault();
    if (section === s) return;
    section = s;
    nav?.push(`/settings/${s}`);
    update();
    root.querySelector<HTMLElement>('.settings-pane h1')?.focus?.();
  }

  /** The owner's own page, mounted once into its part (its bar is hidden: the pane has one). */
  function embed(el: Element | undefined, which: 'owner' | 'storage'): void {
    if (!(el instanceof HTMLElement) || embedded.has(el)) return;
    embedded.add(el);
    if (which === 'owner') mountOwner(el, api, go, location.origin); else mountStorage(el, api, go);
  }

  /** Signed out meanwhile: to sign in, then back to this same part. */
  const signIn = () => go(`/sign-in?next=${encodeURIComponent(section === null ? '/settings' : `/settings/${section}`)}`);

  const friendLink = (u: User) => `${location.origin}/add/${u.gomId}`;
  async function copyLink(u: User): Promise<void> {
    try { await navigator.clipboard.writeText(friendLink(u)); linkStatus = say.friends.copied; } catch { linkStatus = say.friends.copyFailed; }
    update();
  }

  async function unblock(gomId: string): Promise<void> {
    const result = await api.unblock(gomId);
    if (!result.ok) { if (result.error === 'unauthorized') { signIn(); return; } error = result; update(); return; }
    blocked = blocked.filter((p) => p.gomId !== gomId);
    update();
  }

  /**
   * The profile picture, big, with a camera button to choose a photo; the square from its middle that will be kept is shown before
   * it is saved. Without uploads on the server, only the letters.
   */
  const pictureCard = (u: User) => html`
    <div class="me" data-avatar>
      ${picked === null ? html`
        <div class="me-pic">${avatar(u.displayName, false, true, u.avatar)}
          ${u.uploads ? html`
            <label class="me-cam" for="avatar-file" title=${u.avatar ? say.settings.changePicture : say.settings.choosePicture}>${icons.camera()}
              <span class="sr-only">${u.avatar ? say.settings.changePicture : say.settings.choosePicture}</span></label>
            <input class="file-input" id="avatar-file" type="file" accept="image/jpeg,image/png,image/webp" data-avatar-file
              @change=${(e: Event) => { const input = e.target as HTMLInputElement; const file = input.files?.[0]; input.value = ''; if (file) choosePicture(file); }}>` : nothing}
        </div>` : html`
        <img class="avatar-preview" src=${picked.preview} alt=${say.settings.picturePreview} data-avatar-preview>
        <div class="avatar-row">
          <button class="btn small" type="button" data-avatar-save ?disabled=${busy} @click=${() => void savePicture()}>${say.settings.savePicture}</button>
          <button class="btn secondary small" type="button" data-avatar-cancel ?disabled=${busy} @click=${() => { picked = null; update(); }}>${say.common.cancel}</button>
        </div>`}
      <b>${u.displayName}</b><small>@${u.gomId}</small>
      ${picked === null && u.uploads && u.avatar ? html`<button class="linkish" type="button" data-avatar-remove ?disabled=${busy} @click=${() => void removePicture()}>${say.settings.removePicture}</button>` : nothing}
      <p class="hint" role="status" data-avatar-status>${pictureStatus}</p>
    </div>`;

  /** The name others see (opened to change it), the Gom ID, and the link that lets someone send a friend request. */
  const profileRows = (u: User) => html`
    <ul class="kv">
      <li><details class="kv-edit" ?open=${nameOpen} @toggle=${(e: Event) => { nameOpen = (e.target as HTMLDetailsElement).open; }}>
        <summary><span class="t"><small>${say.settings.displayName}</small><b>${u.displayName}</b></span>
          <span class="kv-i" aria-hidden="true">${icons.pencil()}</span><span class="sr-only">${say.settings.editName}</span></summary>
        <form data-nickname @submit=${saveNickname}>
          <label class="sr-only" for="display-name">${say.settings.displayName}</label>
          <div class="input"><input id="display-name" name="displayName" type="text" autocomplete="nickname" maxlength=${limits().nameChars} enterkeyhint="done"
            .value=${nickname} @input=${(e: Event) => { nickname = (e.target as HTMLInputElement).value; }}></div>
          <p class="hint">${say.settings.displayNameHint(u.gomId)}</p>
          <button class="btn" type="submit" ?disabled=${busy}>${say.settings.saveName}</button>
        </form>
      </details></li>
      <li><span class="t"><small>${say.settings.gomId}</small><b>@${u.gomId}</b></span></li>
      <li><span class="t"><small>${say.settings.friendLink}</small><b data-friend-link>${friendLink(u)}</b></span>
        <button class="ib" type="button" data-copy-link aria-label=${say.friends.copyLink} @click=${() => void copyLink(u)}>${icons.copy()}</button></li>
    </ul>
    <p class="hint" role="status" data-nickname-status>${nicknameStatus || linkStatus}</p>
    <p class="hint">${say.settings.profileNote}</p>`;

  /** Notifications for this device: a switch, how much they say, or why this device cannot have them. */
  const pushCard = (p: PushState) => {
    const support = pusher?.support() ?? 'unsupported';
    return html`
      <div class="card" data-push>
        ${support === 'needs-home-screen' ? html`<p class="hint" data-push-home>${say.settings.pushHomeScreen}</p>`
          : support === 'unsupported' ? html`<p class="hint" data-push-unsupported>${say.settings.pushUnsupported}</p>`
          : !p.subscribed && pusher!.permission() === 'denied' ? html`<p class="hint" data-push-blocked>${say.settings.pushBlocked}</p>`
          : html`
            <div class="pick"><input type="checkbox" id="push-on" data-push-switch .checked=${live(p.subscribed)} ?disabled=${pushBusy}
              @change=${(e: Event) => void setPushOn((e.target as HTMLInputElement).checked)}>
              <label for="push-on">${say.settings.pushOn}</label></div>
            ${p.subscribed ? html`
              <fieldset class="choices" data-push-level>
                <legend class="hint">${say.settings.pushShows}</legend>
                ${([['words', say.settings.pushLevelWords], ['name', say.settings.pushLevelName], ['nothing', say.settings.pushLevelNothing]] as const).map(([value, label]) => html`
                  <div class="choice"><input type="radio" name="push-level" id=${`push-${value}`} value=${value} .checked=${live(p.level === value)} ?disabled=${pushBusy}
                    @change=${() => void setPushLevel(value)}><label for=${`push-${value}`}>${label}</label></div>`)}
              </fieldset>` : nothing}
            ${p.subscribed && p.level === 'words' ? html`<p class="hint" data-push-words-note>${say.settings.pushWordsNote}</p>` : nothing}`}
        <p class="hint" role="status" data-push-status>${pushStatus}</p>
      </div>`;
  };

  /** Deleting the account, for good: opened first, then what it does, then the person's own Gom ID typed out before the button works. */
  const deleteCard = (u: User) => {
    const typed = confirmDelete.trim().replace(/^@/, '').toLowerCase();
    return html`
      <h2 class="section">${say.settings.dangerZone}</h2>
      <details class="card danger-zone" data-delete-zone>
        <summary class="btn danger">${say.settings.deleteOpen}</summary>
        <form data-delete-account @submit=${deleteAccount}>
          <p class="hint">${say.settings.deleteNote}</p>
          <label for="delete-confirm">${say.settings.deleteConfirm(u.gomId)}</label>
          <div class="input"><input id="delete-confirm" name="confirm" type="text" autocomplete="off" autocapitalize="none" spellcheck="false"
            .value=${live(confirmDelete)} @input=${(e: Event) => { confirmDelete = (e.target as HTMLInputElement).value; update(); }}></div>
          <button class="btn danger" type="submit" data-delete-button ?disabled=${busy || typed !== u.gomId}>${say.settings.deleteForever}</button>
        </form>
      </details>`;
  };

  /** What a row in the menu says under its title: how that part is set now. */
  const summary = (s: Section): string => {
    switch (s) {
      case 'notifications': {
        const support = pusher?.support() ?? 'unsupported';
        if (push === null || support === 'unsupported') return say.settings.pushSummaryNone;
        return push.subscribed ? say.settings.pushSummary[push.level] : say.settings.pushSummaryOff;
      }
      case 'appearance': return [SCHEMES.find(([v]) => v === scheme)?.[1] ?? '', say.settings.palettes[palette], LANGUAGES.find(([v]) => v === language)?.[1] ?? ''].join(' · ');
      case 'privacy': return say.settings.receiptsSummary(receipts);
      case 'account': return say.settings.accountSummary(sessions.length);
      case 'owner': return say.settings.ownerSummary;
      case 'storage': return say.settings.storageSummary;
      default: return '';
    }
  };

  const ICONS: Record<Section, () => TemplateResult> = {
    profile: icons.users, notifications: icons.bell, appearance: icons.palette, privacy: icons.lock, account: icons.shield, owner: icons.users, storage: icons.disk,
  };

  /** One row of the menu: a link to its part, the open one marked (a wide screen shows it beside the menu). */
  const navRow = (s: Section) => html`
    <li><a href=${`/settings/${s}`} data-section-link=${s} aria-current=${openPart() === s ? 'page' : nothing} @click=${(e: MouseEvent) => open(e, s)}>
      <span class="lead-i">${ICONS[s]()}</span>
      <span class="t"><b>${title(s)}</b><small>${summary(s)}</small></span>
      <span class="chev">${icons.chevron()}</span>
    </a></li>`;

  const menu = (u: User) => html`
    <a class="profile" href="/settings/profile" data-profile data-section-link="profile" aria-current=${openPart() === 'profile' ? 'page' : nothing}
      @click=${(e: MouseEvent) => open(e, 'profile')}>${avatar(u.displayName, false, true, u.avatar)}
      <span class="t"><b>${u.displayName}</b><small>@${u.gomId}</small></span><span class="chev">${icons.chevron()}</span></a>
    <ul class="nav">${(['notifications', 'appearance', 'privacy', 'account'] as const).map(navRow)}</ul>
    ${u.isOwner ? html`<h2 class="section">${say.settings.ownerHeading}</h2><ul class="nav">${(['owner', 'storage'] as const).map(navRow)}</ul>` : nothing}
    ${installer?.available() ? html`
      <ul class="nav"><li><button type="button" data-install-button @click=${installApp}>
        <span class="lead-i">${icons.phone()}</span><span class="t"><b>${say.settings.installRow}</b><small>${say.settings.installSummary}</small></span>
        <span class="chev">${icons.chevron()}</span></button></li></ul>` : nothing}
    ${installStatus === '' ? nothing : html`<p class="hint" role="status" data-install-status>${installStatus}</p>`}
    <button class="btn secondary signout" type="button" data-signout @click=${signOut}>${icons.signOut()}${say.common.signOut}</button>`;

  /** The parts, each in the DOM and shown only when it is the one open (the owner's two are mounted when opened). */
  const part = (s: Section, body: unknown) => html`<section class="part" data-part=${s} aria-label=${title(s)} ?hidden=${openPart() !== s}>${body}</section>`;

  const parts = (u: User) => html`
    ${part('profile', html`${pictureCard(u)}${profileRows(u)}`)}
    ${part('notifications', push === null ? html`<p class="hint" data-push-unsupported>${say.settings.pushUnsupported}</p>` : pushCard(push))}
    ${part('appearance', html`
      <h2 class="section">${say.settings.schemeHeading}</h2>
      <fieldset class="choices" data-scheme>
        <legend class="sr-only">${say.settings.scheme}</legend>
        ${SCHEMES.map(([value, label]) => html`
          <div class="choice"><input type="radio" name="scheme" id=${`scheme-${value}`} value=${value} .checked=${live(scheme === value)}
            @change=${() => { scheme = value; chooseScheme(value); update(); }}>
            <label for=${`scheme-${value}`}>${label}</label></div>`)}
      </fieldset>
      <div class="pick"><input type="checkbox" id="black" data-black-switch aria-describedby="black-note" .checked=${live(black)}
        @change=${(e: Event) => { black = (e.target as HTMLInputElement).checked; applyBlack(black); update(); }}>
        <label for="black">${say.settings.black}</label></div>
      <p class="hint" id="black-note">${say.settings.blackNote}</p>
      <h2 class="section">${say.settings.palette}</h2>
      <fieldset class="swatches" data-palette-choice aria-describedby="palette-note">
        <legend class="sr-only">${say.settings.palette}</legend>
        ${PALETTES.map((value) => html`
          <label class="swatch"><input type="radio" name="palette" value=${value} .checked=${live(palette === value)} ?disabled=${busy}
            @change=${() => void choosePalette(value)}><span class="dot" data-palette=${value} aria-hidden="true"></span><span>${say.settings.palettes[value]}</span></label>`)}
        <p class="hint" id="palette-note">${say.settings.paletteNote}</p>
      </fieldset>
      <h2 class="section">${say.settings.pattern}</h2>
      <fieldset class="patterns" data-pattern-choice aria-describedby="pattern-note">
        <legend class="sr-only">${say.settings.pattern}</legend>
        ${PATTERNS.map((value) => html`
          <label class="pattern-tile"><input type="radio" name="pattern" value=${value} .checked=${live(pattern === value)} ?disabled=${busy}
            @change=${() => void choosePattern(value)}><span class="backdrop" data-pattern=${value} aria-hidden="true"></span><span class="name">${say.settings.patterns[value]}</span></label>`)}
        <p class="hint" id="pattern-note">${say.settings.patternNote}</p>
      </fieldset>
      <h2 class="section">${say.settings.language}</h2>
      <fieldset class="choices" data-language>
        <legend class="sr-only">${say.settings.language}</legend>
        ${LANGUAGES.map(([value, label]) => html`
          <div class="choice"><input type="radio" name="language" id=${`language-${value}`} value=${value} .checked=${live(language === value)}
            ?disabled=${busy} @change=${() => void chooseLanguage(value)}>
            <label for=${`language-${value}`} lang=${value}>${label}</label></div>`)}
      </fieldset>`)}
    ${part('privacy', html`
      <h2 class="section">${say.settings.seen}</h2>
      <div class="card">
        <div class="pick"><input type="checkbox" id="receipts" name="receipts" .checked=${live(receipts)} @change=${(e: Event) => void saveReceipts((e.target as HTMLInputElement).checked)}>
          <label for="receipts">${say.settings.receipts}</label></div>
        <p class="hint">${say.settings.receiptsNote}</p>
        <p class="hint" role="status" data-receipts-status>${receiptsStatus}</p>
      </div>
      <h2 class="section">${say.settings.blocked}</h2>
      ${blocked.length === 0 ? html`<p class="hint" data-blocked-none>${say.settings.blockedNone}</p>` : html`
        <ul class="list">${blocked.map((p) => html`
          <li class="li" data-blocked=${p.gomId}>${avatar(p.displayName, false, false, p.avatar)}
            <div class="t"><b>${p.displayName}</b><small>@${p.gomId}</small></div>
            <button class="btn secondary small" type="button" @click=${() => void unblock(p.gomId)}>${say.friends.unblock}</button></li>`)}</ul>`}
      <p class="hint">${say.settings.blockedNote}</p>`)}
    ${part('account', html`
      <h2 class="section">${say.settings.password}</h2>
      <form class="card" data-password @submit=${savePassword}>
        <input class="sr-only" name="username" type="text" autocomplete="username" readonly tabindex="-1" aria-hidden="true" .value=${u.gomId}>
        ${passwordField({ label: say.settings.currentPassword, name: 'current', autocomplete: 'current-password', value: current, shown,
          onInput: (v) => { current = v; }, onToggle: toggleShown })}
        ${passwordField({ label: say.settings.newPassword, name: 'password', autocomplete: 'new-password', value: next, shown,
          hint: `${passwordHint()} ${say.settings.othersSignedOut}`, onInput: (v) => { next = v; }, onToggle: toggleShown })}
        <button class="btn" type="submit" ?disabled=${busy}>${say.settings.changePassword}</button>
        <p class="hint" role="status" data-password-status>${passwordStatus}</p>
      </form>
      <h2 class="section">${say.settings.devices}</h2>
      <ul class="list">${sessions.map(row)}</ul>
      ${u.isOwner ? nothing : deleteCard(u)}`)}
    ${u.isOwner ? html`
      ${part('owner', openPart() === 'owner' ? html`<div class="embed" ${ref((el) => embed(el, 'owner'))}></div>` : nothing)}
      ${part('storage', openPart() === 'storage' ? html`<div class="embed" ${ref((el) => embed(el, 'storage'))}></div>` : nothing)}` : nothing}`;

  const view = () => html`
    <div class="settings" data-open=${section ?? ''}>
      <div class="settings-menu">
        ${appBar({ title: say.settings.title, back: '/' })}
        <div class="page">
          ${errorBox(error)}
          ${user === null ? nothing : menu(user)}
        </div>
      </div>
      <div class="settings-pane">
        ${appBar({ title: title(openPart()), back: '/settings' })}
        <div class="page">
          ${user === null ? nothing : parts(user)}
        </div>
      </div>
    </div>`;
  const update = () => render(view(), root);

  async function load(): Promise<void> {
    const [result, blocks] = await Promise.all([api.sessions(), api.blocks()]);
    if (result.ok) sessions = result.data; else error = result;
    if (blocks.ok) blocked = blocks.data;
    update();
  }

  /** A member at an owner's part (an old link, say) gets the menu: the part would only show refusals. */
  function guard(): void {
    if (section !== null && OWNER_ONLY.includes(section) && user !== null && !user.isOwner) { section = null; nav?.replace('/settings'); }
  }

  /** The person's colour: drawn at once, then saved with their account; put back, with the reason, if the server says no. */
  async function choosePalette(chosen: Palette): Promise<void> {
    const before = palette;
    palette = chosen; error = null; applyPalette(chosen); update();
    const result = await api.setPalette(chosen);
    if (result.ok) return;
    palette = before; applyPalette(before); error = result; update();
  }

  /** The background behind the person's messages: remembered at once, then saved with their account; put back if the server says no. */
  async function choosePattern(chosen: Pattern): Promise<void> {
    const before = pattern;
    pattern = chosen; error = null; rememberPattern(chosen); update();
    const result = await api.setPattern(chosen);
    if (result.ok) return;
    pattern = before; rememberPattern(before); error = result; update();
  }

  /** The person's language for every device: saved with their account, then this page again in it. */
  async function chooseLanguage(chosen: Language): Promise<void> {
    busy = true; error = null; update();
    const result = await api.setLanguage(chosen);
    busy = false;
    if (!result.ok) { error = result; update(); return; }
    rememberLanguage(chosen);
    go(section === null ? '/settings' : `/settings/${section}`);
  }

  async function saveReceipts(enabled: boolean): Promise<void> {
    receipts = enabled; receiptsStatus = ''; error = null;
    const result = await api.setReceipts(enabled);
    if (result.ok) receiptsStatus = say.settings.saved; else { receipts = !enabled; error = result; }
    update();
  }

  const toggleShown = () => { shown = !shown; update(); };

  async function installApp(): Promise<void> {
    const result = await installer?.install();
    installStatus = result === 'accepted' ? say.settings.installed : '';
    update();
  }

  function choosePicture(file: File): void {
    pictureStatus = '';
    const reader = new FileReader();
    reader.onload = () => { if (typeof reader.result === 'string') { picked = { file, preview: reader.result }; update(); } };
    reader.onerror = () => { pictureStatus = say.errors.invalidImage; update(); };
    reader.readAsDataURL(file);
  }

  async function savePicture(): Promise<void> {
    if (picked === null || busy || user === null) return;
    busy = true; pictureStatus = say.settings.saving; update();
    const result = await api.setAvatar(picked.file);
    busy = false;
    if (result.ok) { user = { ...user, avatar: result.data.avatar }; picked = null; pictureStatus = say.settings.pictureSaved; }
    else if (result.error === 'unauthorized') { signIn(); return; }
    else pictureStatus = errorText(result);
    update();
  }

  async function removePicture(): Promise<void> {
    if (busy || user === null) return;
    busy = true; update();
    const result = await api.removeAvatar();
    busy = false;
    if (result.ok) { user = { ...user, avatar: null }; pictureStatus = say.settings.pictureRemoved; }
    else if (result.error === 'unauthorized') { signIn(); return; }
    else pictureStatus = errorText(result);
    update();
  }

  /** On: ask the browser (this runs in the tap, as iPhones require), then tell the server. Off: both forget this device. */
  async function setPushOn(on: boolean): Promise<void> {
    if (push === null || pusher === undefined || pushBusy) return;
    pushBusy = true; pushStatus = ''; update();
    if (on) {
      const sub = await pusher.subscribe(push.key);
      if (sub === 'denied') pushStatus = say.settings.pushDenied;
      else if (sub === 'failed') pushStatus = say.settings.pushFailed;
      else if (sub === 'no-service') pushStatus = NO_PUSH_SERVICE;
      else {
        const result = await api.setPush(sub, push.level);
        if (result.ok) { push = { ...push, subscribed: true }; pushStatus = say.settings.pushTurnedOn; }
        else if (result.error === 'unauthorized') { signIn(); return; }
        else pushStatus = errorText(result);
      }
    } else {
      await pusher.unsubscribe();
      const result = await api.removePush();
      if (result.ok) { push = { ...push, subscribed: false }; pushStatus = say.settings.pushTurnedOff; }
      else if (result.error === 'unauthorized') { signIn(); return; }
      else pushStatus = errorText(result);
    }
    pushBusy = false;
    update();
  }

  async function setPushLevel(level: PushLevel): Promise<void> {
    if (push === null || pusher === undefined || pushBusy) return;
    pushBusy = true; pushStatus = ''; update();
    const sub = await pusher.current();
    const result = sub === null ? null : await api.setPush(sub, level);
    pushBusy = false;
    if (result?.ok) { push = { ...push, level }; pushStatus = say.settings.saved; }
    else if (result === null) { push = { ...push, subscribed: false }; pushStatus = say.settings.pushGone; }
    else if (result.error === 'unauthorized') { signIn(); return; }
    else pushStatus = errorText(result);
    update();
  }

  async function saveNickname(event: Event): Promise<void> {
    event.preventDefault();
    if (busy) return;
    busy = true; error = null; nicknameStatus = ''; update();
    const result = await api.setNickname(nickname);
    busy = false;
    if (result.ok) { user = { ...user, ...result.data }; nickname = result.data.displayName; nicknameStatus = say.settings.saved; } else error = result;
    update();
  }

  async function savePassword(event: Event): Promise<void> {
    event.preventDefault();
    if (busy) return;
    busy = true; error = null; passwordStatus = ''; update();
    const result = await api.changePassword(current, next);
    busy = false;
    if (!result.ok) { error = result; update(); return; }
    current = ''; next = ''; passwordStatus = say.settings.passwordChanged;
    await load();
  }

  async function endSession(id: string): Promise<void> {
    const result = await api.endSession(id);
    if (!result.ok) { error = result; update(); return; }
    await load();
  }

  async function deleteAccount(event: Event): Promise<void> {
    event.preventDefault();
    if (busy || user === null) return;
    busy = true; error = null; update();
    const result = await api.deleteAccount(confirmDelete.trim());
    busy = false;
    if (result.ok) { go('/sign-in'); return; }
    error = result;
    update();
  }

  async function signOut(): Promise<void> {
    const result = await api.signOut();
    if (result.ok || result.error === 'unauthorized') go('/sign-in'); else { error = result; update(); }
  }

  installer?.onChange(update);
  nav?.onPop(() => { section = sectionOf(nav.path()); guard(); update(); });
  update();
  void api.me().then(async (result) => {
    if (!result.ok) {
      if (result.error === 'unauthorized') signIn(); else { error = result; update(); }
      return;
    }
    user = result.data;
    guard();
    nickname = result.data.displayName;
    receipts = result.data.readReceipts !== false;
    const state = await api.pushState();
    if (state.ok) push = state.data;
    await load();
  });
}
