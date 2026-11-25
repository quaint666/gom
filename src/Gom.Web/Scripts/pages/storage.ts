import { html, nothing, render } from 'lit-html';
import type { Api, Failure, GroupUse, MessageTrimResult, PersonUse, Storage, TrimResult, TrimRule } from '../api.js';
import { formatSize } from '../text.js';
import { appBar, errorBox } from '../ui.js';

/** "3 ảnh và tệp · 1,2 MB" */
const sent = (files: number, bytes: number): string => `${files} ảnh và tệp · ${formatSize(bytes)}`;

/** The word the owner types to trim; the server checks the same. */
const CONFIRM = 'Delete';

/** "2026-01-31" for a date input, in this device's time. */
const isoDay = (d: Date): string => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;

/** "3 ảnh (1,2 MB) và 1 tệp (12 MB)" */
const counted = (r: TrimResult): string => `${r.pictures.count} ảnh (${formatSize(r.pictures.bytes)}) và ${r.files.count} tệp (${formatSize(r.files.bytes)})`;

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
        <b>${g.title ?? 'Nhóm'}${g.members === 0 ? html`<span class="tag">Không còn ai</span>` : nothing}</b>
        <small>${g.members} thành viên · ${g.messages} tin nhắn · ${sent(g.files, g.bytes)}</small>
      </div>
      ${g.members !== 0 ? nothing : confirming === g.id
        ? html`<button class="btn danger small" type="button" data-confirm-delete ?disabled=${busy} @click=${() => remove(g.id)}>Xóa hẳn</button>
               <button class="btn secondary small" type="button" data-cancel-delete @click=${() => { confirming = null; update(); }}>Hủy</button>`
        : html`<button class="btn secondary small" type="button" data-delete-group @click=${() => { confirming = g.id; update(); }}>Xóa nhóm</button>`}
    </li>`;

  const personRow = (p: PersonUse) => html`
    <li class="li" data-person=${p.gomId}>
      <div class="t"><b>${p.displayName}${p.deleted ? html`<span class="tag">Đã xóa</span>` : nothing}</b><small>@${p.gomId} · ${sent(p.files, p.bytes)}</small></div>
    </li>`;

  const body = (s: Storage) => html`
    <ul class="list">
      ${s.disk === null ? nothing : line('Ổ đĩa', `Còn trống ${formatSize(s.disk.free)} / ${formatSize(s.disk.total)}`, 'disk')}
      ${s.uploadsFolder === null ? nothing : line('Thư mục ảnh và tệp', formatSize(s.uploadsFolder), 'folder')}
      ${line('Cơ sở dữ liệu', formatSize(s.database), 'database')}
      ${line('Ảnh đã gửi', `${s.pictures.count} · ${formatSize(s.pictures.bytes)}`, 'pictures')}
      ${line('Tệp đã gửi', `${s.files.count} · ${formatSize(s.files.bytes)}`, 'files')}
    </ul>
    <p class="hint">Thư mục còn chứa ảnh thu nhỏ và ảnh đại diện, nên lớn hơn tổng ảnh và tệp đã gửi.</p>
    <h2 class="section">Nhóm</h2>
    ${s.groups.length === 0 ? html`<p class="hint" data-no-group>Chưa có nhóm nào.</p>` : html`<ul class="list">${s.groups.map(groupRow)}</ul>`}
    <h2 class="section">Trò chuyện 1-1</h2>
    <ul class="list">${line(`${s.direct.conversations} cuộc trò chuyện`, `${s.direct.messages} tin nhắn · ${sent(s.direct.files, s.direct.bytes)}`, 'direct')}</ul>
    <p class="hint">Tính gộp, không ghi ai trò chuyện với ai.</p>
    <h2 class="section">Theo người gửi</h2>
    ${s.people.length === 0 ? html`<p class="hint" data-no-person>Chưa ai gửi ảnh hay tệp.</p>` : html`<ul class="list">${s.people.map(personRow)}</ul>`}
    ${trimCard(s)}
    ${messageTrimCard(s)}`;

  const rule = (): TrimRule => ({
    before: new Date(`${trim.day}T00:00`).toISOString(), kind: trim.kind, where: trim.where,
    minBytes: trim.minMb.trim() === '' ? null : Math.round(Number(trim.minMb) * 1024 * 1024),
  });
  const choose = (change: Partial<typeof trim>) => { trim = { ...trim, ...change, preview: null, typed: '', done: '' }; update(); };
  const total = (r: TrimResult) => r.pictures.count + r.files.count;

  /** Where a trim may reach: everywhere, every one-to-one chat together (never one alone), or one group. */
  const places = (s: Storage, current: string) => html`
    <option value="all" ?selected=${current === 'all'}>Mọi cuộc trò chuyện</option>
    <option value="direct" ?selected=${current === 'direct'}>Mọi trò chuyện 1-1</option>
    ${s.groups.map((g) => html`<option value=${g.id} ?selected=${current === g.id}>Nhóm: ${g.title ?? 'Nhóm'}</option>`)}`;

  const mrule = () => ({ before: new Date(`${mtrim.day}T00:00`).toISOString(), where: mtrim.where });
  const mchoose = (change: Partial<typeof mtrim>) => { mtrim = { ...mtrim, ...change, preview: null, typed: '', done: '' }; update(); };
  /** "347 tin nhắn, kèm 120 ảnh (400 MB) và 3 tệp (10 MB)" */
  const mcounted = (r: MessageTrimResult) => `${r.messages} tin nhắn, kèm ${counted(r)}`;

  const messageTrimCard = (s: Storage) => html`
    <h2 class="section">Dọn tin nhắn</h2>
    <div class="card" data-mtrim>
      <div class="field"><label for="mtrim-day">Gửi trước ngày</label>
        <input id="mtrim-day" name="mtrim-day" type="date" max=${isoDay(new Date())} .value=${mtrim.day} @change=${(e: Event) => mchoose({ day: (e.target as HTMLInputElement).value })}></div>
      <div class="field"><label for="mtrim-where">Ở đâu</label>
        <select id="mtrim-where" name="mtrim-where" @change=${(e: Event) => mchoose({ where: (e.target as HTMLSelectElement).value })}>${places(s, mtrim.where)}</select></div>
      <button class="btn secondary" type="button" data-mtrim-preview ?disabled=${mtrim.day === '' || mtrim.busy} @click=${() => void mpreview()}>Xem trước</button>
      ${mtrim.preview === null ? nothing : mtrim.preview.messages === 0 && total(mtrim.preview) === 0
        ? html`<p class="hint" data-mtrim-none>Không có tin nhắn nào khớp.</p>`
        : html`
          <p data-mtrim-summary>Sẽ xóa vĩnh viễn ${mcounted(mtrim.preview)}, trong ${mtrim.preview.conversations} cuộc trò chuyện.</p>
          <p class="hint">Không thể hoàn tác, kể cả trong bản sao lưu. Câu trả lời mới hơn vẫn còn, kèm dòng "Tin nhắn không còn hiển thị"; mỗi cuộc trò chuyện có một dòng thông báo.</p>
          <div class="field"><label for="mtrim-confirm">Gõ ${CONFIRM} để xác nhận</label>
            <input id="mtrim-confirm" name="mtrim-confirm" type="text" autocomplete="off" autocapitalize="off" spellcheck="false" .value=${mtrim.typed}
              @input=${(e: Event) => { mtrim.typed = (e.target as HTMLInputElement).value; update(); }}></div>
          <button class="btn danger" type="button" data-mtrim-confirm ?disabled=${mtrim.typed !== CONFIRM || mtrim.busy} @click=${() => void runMessageTrim()}>Xóa vĩnh viễn</button>`}
      ${mtrim.done === '' ? nothing : html`<p class="hint" role="status" data-mtrim-done>${mtrim.done}</p>`}
    </div>`;

  const trimCard = (s: Storage) => html`
    <h2 class="section">Dọn ảnh và tệp</h2>
    <div class="card" data-trim>
      <div class="field"><label for="trim-day">Gửi trước ngày</label>
        <input id="trim-day" name="trim-day" type="date" max=${isoDay(new Date())} .value=${trim.day} @change=${(e: Event) => choose({ day: (e.target as HTMLInputElement).value })}></div>
      <div class="field"><label for="trim-kind">Loại</label>
        <select id="trim-kind" name="trim-kind" @change=${(e: Event) => choose({ kind: (e.target as HTMLSelectElement).value as TrimRule['kind'] })}>
          <option value="all" ?selected=${trim.kind === 'all'}>Ảnh và tệp</option>
          <option value="image" ?selected=${trim.kind === 'image'}>Chỉ ảnh</option>
          <option value="file" ?selected=${trim.kind === 'file'}>Chỉ tệp</option></select></div>
      <div class="field"><label for="trim-where">Ở đâu</label>
        <select id="trim-where" name="trim-where" @change=${(e: Event) => choose({ where: (e.target as HTMLSelectElement).value })}>
          ${places(s, trim.where)}</select></div>
      <div class="field"><label for="trim-min">Chỉ khi lớn hơn (MB)</label>
        <input id="trim-min" name="trim-min" type="number" min="0" step="0.1" inputmode="decimal" placeholder="Mọi kích thước" .value=${trim.minMb}
          @change=${(e: Event) => choose({ minMb: (e.target as HTMLInputElement).value })}></div>
      <button class="btn secondary" type="button" data-trim-preview ?disabled=${trim.day === '' || trim.busy} @click=${() => void preview()}>Xem trước</button>
      ${trim.preview === null ? nothing : total(trim.preview) === 0
        ? html`<p class="hint" data-trim-none>Không có ảnh hay tệp nào khớp.</p>`
        : html`
          <p data-trim-summary>Sẽ xóa vĩnh viễn ${counted(trim.preview)} trong ${trim.preview.conversations} cuộc trò chuyện.</p>
          <p class="hint">Không thể hoàn tác, kể cả trong bản sao lưu. Tin nhắn vẫn còn, kèm dòng "không còn khả dụng"; mỗi cuộc trò chuyện có một dòng thông báo.</p>
          <div class="field"><label for="trim-confirm">Gõ ${CONFIRM} để xác nhận</label>
            <input id="trim-confirm" name="trim-confirm" type="text" autocomplete="off" autocapitalize="off" spellcheck="false" .value=${trim.typed}
              @input=${(e: Event) => { trim.typed = (e.target as HTMLInputElement).value; update(); }}></div>
          <button class="btn danger" type="button" data-trim-confirm ?disabled=${trim.typed !== CONFIRM || trim.busy} @click=${() => void runTrim()}>Xóa vĩnh viễn</button>`}
      ${trim.done === '' ? nothing : html`<p class="hint" role="status" data-trim-done>${trim.done}</p>`}
    </div>`;

  const view = () => html`
    ${appBar({ title: 'Dung lượng', back: '/settings' })}
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
    trim = { ...trim, preview: null, typed: '', done: `Đã xóa ${counted(result.data)} trong ${result.data.conversations} cuộc trò chuyện.` };
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
    mtrim = { ...mtrim, preview: null, typed: '', done: `Đã xóa ${mcounted(result.data)}, trong ${result.data.conversations} cuộc trò chuyện.` };
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
