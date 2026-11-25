import { html, nothing, render } from 'lit-html';
import { live } from 'lit-html/directives/live.js';
import type { Api, Failure, PushLevel, PushState, SessionInfo, User } from '../api.js';
import { chosenScheme, chooseScheme, type Choice } from '../theme.js';
import type { Installer } from '../install.js';
import { NO_PUSH_SERVICE, type Pusher } from '../push.js';
import { appBar, avatar, errorBox, formatTime, passwordField } from '../ui.js';
import { errorText, passwordHint } from '../text.js';
import { LANGUAGES, language, rememberLanguage, say, type Language } from '../say.js';
import { limits } from '../limits.js';

/** The choices for the colour scheme, in the order shown. */
const SCHEMES: ReadonlyArray<readonly [Choice, string]> = [['system', say.settings.schemeSystem], ['light', say.settings.schemeLight], ['dark', say.settings.schemeDark]];

export function mountSettings(root: HTMLElement, api: Api, go: (path: string) => void, installer?: Installer, pusher?: Pusher): void {
  let user: User | null = null;
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

  /**
   * The profile picture: choose a photo, see the square from its middle that will be kept (a round preview), then save; or go back
   * to the initials.
   */
  const pictureCard = (u: User) => html`
    <div class="card" data-avatar>
      <p><b>${say.settings.picture}</b></p>
      ${picked === null ? html`
        <div class="avatar-row">
          <label class="btn secondary small" for="avatar-file">${u.avatar ? say.settings.changePicture : say.settings.choosePicture}</label>
          <input class="file-input" id="avatar-file" type="file" accept="image/jpeg,image/png,image/webp" data-avatar-file
            @change=${(e: Event) => { const input = e.target as HTMLInputElement; const file = input.files?.[0]; input.value = ''; if (file) choosePicture(file); }}>
          ${u.avatar ? html`<button class="btn secondary small" type="button" data-avatar-remove ?disabled=${busy} @click=${() => void removePicture()}>${say.settings.removePicture}</button>` : nothing}
        </div>
        <p class="hint">${say.settings.pictureNote}</p>` : html`
        <div class="avatar-row">
          <img class="avatar-preview" src=${picked.preview} alt=${say.settings.picturePreview} data-avatar-preview>
          <button class="btn small" type="button" data-avatar-save ?disabled=${busy} @click=${() => void savePicture()}>${say.settings.savePicture}</button>
          <button class="btn secondary small" type="button" data-avatar-cancel ?disabled=${busy} @click=${() => { picked = null; update(); }}>${say.common.cancel}</button>
        </div>`}
      <p class="hint" role="status" data-avatar-status>${pictureStatus}</p>
    </div>`;

  /** Notifications for this device: a switch, how much they say, or why this device cannot have them. */
  const pushCard = (p: PushState) => {
    const support = pusher?.support() ?? 'unsupported';
    return html`
      <h2 class="section">${say.settings.notifications}</h2>
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

  /** Deleting the account, for good: what it does, then the person's own Gom ID typed out before the button can be pressed. */
  const deleteCard = (u: User) => {
    const typed = confirmDelete.trim().replace(/^@/, '').toLowerCase();
    return html`
      <h2 class="section">${say.settings.deleteAccount}</h2>
      <form class="card" data-delete-account @submit=${deleteAccount}>
        <p class="hint">${say.settings.deleteNote}</p>
        <label for="delete-confirm">${say.settings.deleteConfirm(u.gomId)}</label>
        <div class="input"><input id="delete-confirm" name="confirm" type="text" autocomplete="off" autocapitalize="none" spellcheck="false"
          .value=${live(confirmDelete)} @input=${(e: Event) => { confirmDelete = (e.target as HTMLInputElement).value; update(); }}></div>
        <button class="btn danger" type="submit" data-delete-button ?disabled=${busy || typed !== u.gomId}>${say.settings.deleteForever}</button>
      </form>`;
  };

  const view = () => html`
    ${appBar({ title: say.settings.title, back: '/' })}
    <div class="page">
      ${errorBox(error)}
      ${user === null ? nothing : html`
        <div class="profile" data-profile>${avatar(user.displayName, false, true, user.avatar)}
          <div class="t"><b>${user.displayName}</b><small>@${user.gomId}</small></div></div>
        <h2 class="section">${say.settings.profile}</h2>
        ${user.uploads ? pictureCard(user) : nothing}
        <form class="card" data-nickname @submit=${saveNickname}>
          <label for="display-name">${say.settings.displayName}</label>
          <div class="input"><input id="display-name" name="displayName" type="text" autocomplete="nickname" maxlength=${limits().nameChars} enterkeyhint="done"
            .value=${nickname} @input=${(e: Event) => { nickname = (e.target as HTMLInputElement).value; }}></div>
          <p class="hint">${say.settings.displayNameHint(user.gomId)}</p>
          <button class="btn" type="submit" ?disabled=${busy}>${say.settings.saveName}</button>
          <p class="hint" role="status" data-nickname-status>${nicknameStatus}</p>
        </form>
        ${installer?.available() ? html`
          <h2 class="section">${say.settings.app}</h2>
          <div class="card" data-install>
            <p class="hint">${say.settings.installNote}</p>
            <button class="btn" type="button" data-install-button @click=${installApp}>${say.settings.install}</button>
          </div>` : installStatus === '' ? nothing : html`<p class="hint" role="status" data-install-status>${installStatus}</p>`}
        ${push === null ? nothing : pushCard(push)}
        <h2 class="section">${say.settings.appearance}</h2>
        <fieldset class="choices" data-scheme>
          <legend class="sr-only">${say.settings.scheme}</legend>
          ${SCHEMES.map(([value, label]) => html`
            <div class="choice"><input type="radio" name="scheme" id=${`scheme-${value}`} value=${value} .checked=${live(scheme === value)}
              @change=${() => { scheme = value; chooseScheme(value); update(); }}>
              <label for=${`scheme-${value}`}>${label}</label></div>`)}
        </fieldset>
        <fieldset class="choices" data-language>
          <legend class="hint">${say.settings.language}</legend>
          ${LANGUAGES.map(([value, label]) => html`
            <div class="choice"><input type="radio" name="language" id=${`language-${value}`} value=${value} .checked=${live(language === value)}
              ?disabled=${busy} @change=${() => void chooseLanguage(value)}>
              <label for=${`language-${value}`} lang=${value}>${label}</label></div>`)}
        </fieldset>
        <h2 class="section">${say.settings.privacy}</h2>
        <div class="card">
          <div class="pick"><input type="checkbox" id="receipts" name="receipts" .checked=${live(receipts)} @change=${(e: Event) => void saveReceipts((e.target as HTMLInputElement).checked)}>
            <label for="receipts">${say.settings.receipts}</label></div>
          <p class="hint">${say.settings.receiptsNote}</p>
          <p class="hint" role="status" data-receipts-status>${receiptsStatus}</p>
        </div>
        <h2 class="section">${say.settings.security}</h2>
        <form class="card" data-password @submit=${savePassword}>
          <input class="sr-only" name="username" type="text" autocomplete="username" readonly tabindex="-1" aria-hidden="true" .value=${user.gomId}>
          ${passwordField({ label: say.settings.currentPassword, name: 'current', autocomplete: 'current-password', value: current, shown,
            onInput: (v) => { current = v; }, onToggle: toggleShown })}
          ${passwordField({ label: say.settings.newPassword, name: 'password', autocomplete: 'new-password', value: next, shown,
            hint: `${passwordHint()} ${say.settings.othersSignedOut}`, onInput: (v) => { next = v; }, onToggle: toggleShown })}
          <button class="btn" type="submit" ?disabled=${busy}>${say.settings.changePassword}</button>
          <p class="hint" role="status" data-password-status>${passwordStatus}</p>
        </form>
        <h2 class="section">${say.settings.devices}</h2>
        <ul class="list">${sessions.map(row)}</ul>
        <button class="btn secondary" type="button" data-signout @click=${signOut}>${say.common.signOut}</button>
        ${user.isOwner ? html`<a class="btn secondary" href="/owner">${say.common.ownerPage}</a><a class="btn secondary" href="/owner/storage" data-storage-link>${say.settings.storage}</a>` : deleteCard(user)}`}
    </div>`;
  const update = () => render(view(), root);

  async function load(): Promise<void> {
    const result = await api.sessions();
    if (result.ok) sessions = result.data; else error = result;
    update();
  }

  /** The person's language for every device: saved with their account, then this page again in it. */
  async function chooseLanguage(chosen: Language): Promise<void> {
    busy = true; error = null; update();
    const result = await api.setLanguage(chosen);
    busy = false;
    if (!result.ok) { error = result; update(); return; }
    rememberLanguage(chosen);
    go('/settings');
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
    else if (result.error === 'unauthorized') { go('/sign-in'); return; }
    else pictureStatus = errorText(result);
    update();
  }

  async function removePicture(): Promise<void> {
    if (busy || user === null) return;
    busy = true; update();
    const result = await api.removeAvatar();
    busy = false;
    if (result.ok) { user = { ...user, avatar: null }; pictureStatus = say.settings.pictureRemoved; }
    else if (result.error === 'unauthorized') { go('/sign-in'); return; }
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
        else if (result.error === 'unauthorized') { go('/sign-in'); return; }
        else pushStatus = errorText(result);
      }
    } else {
      await pusher.unsubscribe();
      const result = await api.removePush();
      if (result.ok) { push = { ...push, subscribed: false }; pushStatus = say.settings.pushTurnedOff; }
      else if (result.error === 'unauthorized') { go('/sign-in'); return; }
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
    else if (result.error === 'unauthorized') { go('/sign-in'); return; }
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
  update();
  void api.me().then(async (result) => {
    if (!result.ok) {
      if (result.error === 'unauthorized') go('/sign-in'); else { error = result; update(); }
      return;
    }
    user = result.data;
    nickname = result.data.displayName;
    receipts = result.data.readReceipts !== false;
    const state = await api.pushState();
    if (state.ok) push = state.data;
    await load();
  });
}
