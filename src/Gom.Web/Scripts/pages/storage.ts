import { html, nothing, render } from 'lit-html';
import type { Api, Failure, GroupUse, PersonUse, Storage } from '../api.js';
import { formatSize } from '../text.js';
import { appBar, errorBox } from '../ui.js';

/** "3 ảnh và tệp · 1,2 MB" */
const sent = (files: number, bytes: number): string => `${files} ảnh và tệp · ${formatSize(bytes)}`;

/**
 * The owner's storage page: the disk, the database, pictures and files by group, one-to-one chats only together, and by person.
 * A group nobody is in (its last member deleted their account) can be deleted here, after a second tap.
 */
export function mountStorage(root: HTMLElement, api: Api, go: (path: string) => void): void {
  let storage: Storage | null = null;
  let error: Failure | null = null;
  let confirming: string | null = null;
  let busy = false;

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
    ${s.people.length === 0 ? html`<p class="hint" data-no-person>Chưa ai gửi ảnh hay tệp.</p>` : html`<ul class="list">${s.people.map(personRow)}</ul>`}`;

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
