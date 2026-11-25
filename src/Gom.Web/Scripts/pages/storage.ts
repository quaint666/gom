import { html, nothing, render } from 'lit-html';
import type { Api, Failure, GroupUse, MessageTrimResult, PersonUse, Storage, TrimResult, TrimRule } from '../api.js';
import { formatSize } from '../text.js';
import { appBar, errorBox } from '../ui.js';
import { say } from '../say.js';

/** "3 ảnh và tệp · 1,2 MB" */
const sent = (files: number, bytes: number): string => say.storage.sent(files, formatSize(bytes));

/** The word the owner types to trim; the server checks the same. */
const CONFIRM = 'Delete';

/** "2026-01-31" for a date input, in this device's time. */
const isoDay = (d: Date): string => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;

/** "3 ảnh (1,2 MB), 1 video (30 MB) và 1 tệp (12 MB)" */
const counted = (r: TrimResult): string =>
  say.storage.counted(r.pictures.count, formatSize(r.pictures.bytes), r.videos.count, formatSize(r.videos.bytes), r.files.count, formatSize(r.files.bytes));

/**
 * The owner's storage page: the disk, the database, pictures and files by group, one-to-one chats only together, and by person.
 * A group nobody is in (its last member deleted their account) can be deleted here, after a second tap. Old pictures and files, and
 * old messages, can be trimmed: a preview first, then "Delete" typed; gone for good, backups included.
 */
export function mountStorage(root: HTMLElement, api: Api, go: (path: string) => void): void {
  let storage: Storage | null = null;
  let error: Failure | null = null;
  let confirming: string | null = null;
  let busy = false;
  /** The trim form. Changing any choice drops the preview, so what is confirmed is always what was previewed. */
  let trim = { day: '', kind: 'all' as TrimRule['kind'], where: 'all', minMb: '', preview: null as TrimResult | null, typed: '', busy: false, done: '' };
  /** The message trim form, the same way. */
  let mtrim = { day: '', where: 'all', preview: null as MessageTrimResult | null, typed: '', busy: false, done: '' };

  const line = (label: string, value: string, key: string) => html`<li class="li" data-total=${key}><div class="t"><b>${label}</b><small>${value}</small></div></li>`;

  const groupRow = (g: GroupUse) => html`
    <li class="li" data-group=${g.id}>
      <div class="t">
        <b>${g.title ?? say.storage.group}${g.members === 0 ? html`<span class="tag">${say.storage.nobodyLeft}</span>` : nothing}</b>
        <small>${say.storage.groupLine(g.members, g.messages, sent(g.files, g.bytes))}</small>
      </div>
      ${g.members !== 0 ? nothing : confirming === g.id
        ? html`<button class="btn danger small" type="button" data-confirm-delete ?disabled=${busy} @click=${() => remove(g.id)}>${say.storage.deleteForGood}</button>
               <button class="btn secondary small" type="button" data-cancel-delete @click=${() => { confirming = null; update(); }}>${say.common.cancel}</button>`
        : html`<button class="btn secondary small" type="button" data-delete-group @click=${() => { confirming = g.id; update(); }}>${say.storage.deleteGroup}</button>`}
    </li>`;

  const personRow = (p: PersonUse) => html`
    <li class="li" data-person=${p.gomId}>
      <div class="t"><b>${p.displayName}${p.deleted ? html`<span class="tag">${say.storage.deleted}</span>` : nothing}</b><small>@${p.gomId} · ${sent(p.files, p.bytes)}</small></div>
    </li>`;

  const body = (s: Storage) => html`
    <ul class="list">
      ${s.disk === null ? nothing : line(say.storage.disk, say.storage.diskFree(formatSize(s.disk.free), formatSize(s.disk.total)), 'disk')}
      ${s.uploadsFolder === null ? nothing : line(say.storage.folder, formatSize(s.uploadsFolder), 'folder')}
      ${line(say.storage.database, formatSize(s.database), 'database')}
      ${line(say.storage.picturesSent, `${s.pictures.count} · ${formatSize(s.pictures.bytes)}`, 'pictures')}
      ${line(say.storage.videosSent, `${s.videos.count} · ${formatSize(s.videos.bytes)}`, 'videos')}
      ${line(say.storage.filesSent, `${s.files.count} · ${formatSize(s.files.bytes)}`, 'files')}
    </ul>
    <p class="hint">${say.storage.folderNote}</p>
    <h2 class="section">${say.storage.groups}</h2>
    ${s.groups.length === 0 ? html`<p class="hint" data-no-group>${say.storage.noGroup}</p>` : html`<ul class="list">${s.groups.map(groupRow)}</ul>`}
    <h2 class="section">${say.storage.direct}</h2>
    <ul class="list">${line(say.storage.directChats(s.direct.conversations), say.storage.directLine(s.direct.messages, sent(s.direct.files, s.direct.bytes)), 'direct')}</ul>
    <p class="hint">${say.storage.directNote}</p>
    <h2 class="section">${say.storage.bySender}</h2>
    ${s.people.length === 0 ? html`<p class="hint" data-no-person>${say.storage.noSender}</p>` : html`<ul class="list">${s.people.map(personRow)}</ul>`}
    ${trimCard(s)}
    ${messageTrimCard(s)}`;

  const rule = (): TrimRule => ({
    before: new Date(`${trim.day}T00:00`).toISOString(), kind: trim.kind, where: trim.where,
    minBytes: trim.minMb.trim() === '' ? null : Math.round(Number(trim.minMb) * 1024 * 1024),
  });
  const choose = (change: Partial<typeof trim>) => { trim = { ...trim, ...change, preview: null, typed: '', done: '' }; update(); };
  const total = (r: TrimResult) => r.pictures.count + r.videos.count + r.files.count;

  /** Where a trim may reach: everywhere, every one-to-one chat together (never one alone), or one group. */
  const places = (s: Storage, current: string) => html`
    <option value="all" ?selected=${current === 'all'}>${say.storage.everywhere}</option>
    <option value="direct" ?selected=${current === 'direct'}>${say.storage.allDirect}</option>
    ${s.groups.map((g) => html`<option value=${g.id} ?selected=${current === g.id}>${say.storage.inGroup(g.title ?? say.storage.group)}</option>`)}`;

  const mrule = () => ({ before: new Date(`${mtrim.day}T00:00`).toISOString(), where: mtrim.where });
  const mchoose = (change: Partial<typeof mtrim>) => { mtrim = { ...mtrim, ...change, preview: null, typed: '', done: '' }; update(); };
  /** "347 tin nhắn, kèm 120 ảnh (400 MB) và 3 tệp (10 MB)" */
  const mcounted = (r: MessageTrimResult) => say.storage.messagesWith(r.messages, counted(r));

  const messageTrimCard = (s: Storage) => html`
    <h2 class="section">${say.storage.trimMessages}</h2>
    <div class="card" data-mtrim>
      <div class="field"><label for="mtrim-day">${say.storage.sentBefore}</label>
        <input id="mtrim-day" name="mtrim-day" type="date" max=${isoDay(new Date())} .value=${mtrim.day} @change=${(e: Event) => mchoose({ day: (e.target as HTMLInputElement).value })}></div>
      <div class="field"><label for="mtrim-where">${say.storage.where}</label>
        <select id="mtrim-where" name="mtrim-where" @change=${(e: Event) => mchoose({ where: (e.target as HTMLSelectElement).value })}>${places(s, mtrim.where)}</select></div>
      <button class="btn secondary" type="button" data-mtrim-preview ?disabled=${mtrim.day === '' || mtrim.busy} @click=${() => void mpreview()}>${say.storage.preview}</button>
      ${mtrim.preview === null ? nothing : mtrim.preview.messages === 0 && total(mtrim.preview) === 0
        ? html`<p class="hint" data-mtrim-none>${say.storage.noMessageMatches}</p>`
        : html`
          <p data-mtrim-summary>${say.storage.willDeleteMessages(mcounted(mtrim.preview), mtrim.preview.conversations)}</p>
          <p class="hint">${say.storage.messagesNote}</p>
          <div class="field"><label for="mtrim-confirm">${say.storage.typeToConfirm(CONFIRM)}</label>
            <input id="mtrim-confirm" name="mtrim-confirm" type="text" autocomplete="off" autocapitalize="off" spellcheck="false" .value=${mtrim.typed}
              @input=${(e: Event) => { mtrim.typed = (e.target as HTMLInputElement).value; update(); }}></div>
          <button class="btn danger" type="button" data-mtrim-confirm ?disabled=${mtrim.typed !== CONFIRM || mtrim.busy} @click=${() => void runMessageTrim()}>${say.storage.deleteForever}</button>`}
      ${mtrim.done === '' ? nothing : html`<p class="hint" role="status" data-mtrim-done>${mtrim.done}</p>`}
    </div>`;

  const trimCard = (s: Storage) => html`
    <h2 class="section">${say.storage.trimFiles}</h2>
    <div class="card" data-trim>
      <div class="field"><label for="trim-day">${say.storage.sentBefore}</label>
        <input id="trim-day" name="trim-day" type="date" max=${isoDay(new Date())} .value=${trim.day} @change=${(e: Event) => choose({ day: (e.target as HTMLInputElement).value })}></div>
      <div class="field"><label for="trim-kind">${say.storage.kind}</label>
        <select id="trim-kind" name="trim-kind" @change=${(e: Event) => choose({ kind: (e.target as HTMLSelectElement).value as TrimRule['kind'] })}>
          <option value="all" ?selected=${trim.kind === 'all'}>${say.storage.picturesAndFiles}</option>
          <option value="image" ?selected=${trim.kind === 'image'}>${say.storage.picturesOnly}</option>
          <option value="video" ?selected=${trim.kind === 'video'}>${say.storage.videosOnly}</option>
          <option value="file" ?selected=${trim.kind === 'file'}>${say.storage.filesOnly}</option></select></div>
      <div class="field"><label for="trim-where">${say.storage.where}</label>
        <select id="trim-where" name="trim-where" @change=${(e: Event) => choose({ where: (e.target as HTMLSelectElement).value })}>
          ${places(s, trim.where)}</select></div>
      <div class="field"><label for="trim-min">${say.storage.largerThan}</label>
        <input id="trim-min" name="trim-min" type="number" min="0" step="0.1" inputmode="decimal" placeholder=${say.storage.anySize} .value=${trim.minMb}
          @change=${(e: Event) => choose({ minMb: (e.target as HTMLInputElement).value })}></div>
      <button class="btn secondary" type="button" data-trim-preview ?disabled=${trim.day === '' || trim.busy} @click=${() => void preview()}>${say.storage.preview}</button>
      ${trim.preview === null ? nothing : total(trim.preview) === 0
        ? html`<p class="hint" data-trim-none>${say.storage.noFileMatches}</p>`
        : html`
          <p data-trim-summary>${say.storage.willDeleteFiles(counted(trim.preview), trim.preview.conversations)}</p>
          <p class="hint">${say.storage.filesNote}</p>
          <div class="field"><label for="trim-confirm">${say.storage.typeToConfirm(CONFIRM)}</label>
            <input id="trim-confirm" name="trim-confirm" type="text" autocomplete="off" autocapitalize="off" spellcheck="false" .value=${trim.typed}
              @input=${(e: Event) => { trim.typed = (e.target as HTMLInputElement).value; update(); }}></div>
          <button class="btn danger" type="button" data-trim-confirm ?disabled=${trim.typed !== CONFIRM || trim.busy} @click=${() => void runTrim()}>${say.storage.deleteForever}</button>`}
      ${trim.done === '' ? nothing : html`<p class="hint" role="status" data-trim-done>${trim.done}</p>`}
    </div>`;

  const view = () => html`
    ${appBar({ title: say.storage.title, back: '/settings' })}
    <div class="page">
      ${errorBox(error)}
      ${storage === null ? nothing : body(storage)}
    </div>`;
  const update = () => render(view(), root);

  async function load(): Promise<void> {
    const result = await api.storage();
    if (result.ok) storage = result.data; else error = result;
    update();
  }

  async function preview(): Promise<void> {
    trim.busy = true; update();
    const result = await api.trimPreview(rule());
    trim.busy = false;
    if (result.ok) { trim.preview = result.data; error = null; } else error = result;
    update();
  }

  async function runTrim(): Promise<void> {
    trim.busy = true; update();
    const result = await api.trimFiles(rule(), trim.typed);
    trim.busy = false;
    if (!result.ok) { error = result; update(); return; }
    trim = { ...trim, preview: null, typed: '', done: say.storage.deletedFiles(counted(result.data), result.data.conversations) };
    error = null;
    await load();
  }

  async function mpreview(): Promise<void> {
    mtrim.busy = true; update();
    const result = await api.trimMessagesPreview(mrule());
    mtrim.busy = false;
    if (result.ok) { mtrim.preview = result.data; error = null; } else error = result;
    update();
  }

  async function runMessageTrim(): Promise<void> {
    mtrim.busy = true; update();
    const result = await api.trimMessages(mrule(), mtrim.typed);
    mtrim.busy = false;
    if (!result.ok) { error = result; update(); return; }
    mtrim = { ...mtrim, preview: null, typed: '', done: say.storage.deletedMessages(mcounted(result.data), result.data.conversations) };
    error = null;
    await load();
  }

  async function remove(id: string): Promise<void> {
    busy = true; update();
    const result = await api.deleteEmptyGroup(id);
    busy = false; confirming = null;
    if (!result.ok) { error = result; update(); return; }
    error = null;
    await load();
  }

  update();
  void api.me().then(async (result) => {
    if (!result.ok) {
      if (result.error === 'unauthorized') go('/sign-in'); else { error = result; update(); }
      return;
    }
    if (!result.data.isOwner) { go('/'); return; }
    await load();
  });
}
