import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mountStorage } from '../Scripts/pages/storage.js';
import { formatSize } from '../Scripts/text.js';
import { fail, fakeApi, flush, ok, text } from './helpers.js';

let root: HTMLElement;
beforeEach(() => { root = document.createElement('div'); document.body.replaceChildren(root); });

const owner = { gomId: 'boss', displayName: 'Boss', isOwner: true };
const MB = 1024 * 1024;
const storage = {
  disk: { total: 25 * 1024 * MB, free: 15 * 1024 * MB }, database: 9 * MB, uploadsFolder: 14 * MB,
  pictures: { count: 29, bytes: 1.2 * MB }, files: { count: 4, bytes: 12 * MB },
  direct: { conversations: 2, messages: 180, files: 33, bytes: 13 * MB },
  groups: [
    { id: 'g1', title: 'Nhà mình', members: 4, messages: 30, files: 0, bytes: 0 },
    { id: 'g2', title: 'Bỏ trống', members: 0, messages: 5, files: 1, bytes: 2 * MB },
  ],
  people: [{ gomId: 'lan', displayName: 'Lan', deleted: false, files: 33, bytes: 13 * MB }, { gomId: 'x1', displayName: 'Tài khoản đã xóa', deleted: true, files: 1, bytes: MB }],
};

function api(extra = {}) {
  return fakeApi({ me: vi.fn(async () => ok(owner)), storage: vi.fn(async () => ok(storage)), ...extra });
}

describe('storage page', () => {
  it('sends a member away', async () => {
    const go = vi.fn();
    mountStorage(root, fakeApi({ me: vi.fn(async () => ok({ ...owner, isOwner: false })) }), go);
    await flush();
    expect(go).toHaveBeenCalledWith('/');
  });

  it('shows the disk, the folder, the database and the totals', async () => {
    mountStorage(root, api(), vi.fn());
    await flush();
    expect(text(root.querySelector('[data-total="disk"]')!)).toContain('Còn trống 15 GB / 25 GB');
    expect(text(root.querySelector('[data-total="folder"]')!)).toContain('14 MB');
    expect(text(root.querySelector('[data-total="database"]')!)).toContain('9 MB');
    expect(text(root.querySelector('[data-total="files"]')!)).toContain('4 · 12 MB');
  });

  it('counts one-to-one chats together, with no names', async () => {
    mountStorage(root, api(), vi.fn());
    await flush();
    expect(text(root.querySelector('[data-total="direct"]')!)).toContain('2 cuộc trò chuyện');
    expect(text(root.querySelector('[data-total="direct"]')!)).toContain('180 tin nhắn · 33 ảnh và tệp · 13 MB');
  });

  it('lists people, marking a deleted account', async () => {
    mountStorage(root, api(), vi.fn());
    await flush();
    expect(text(root.querySelector('[data-person="lan"]')!)).toContain('@lan · 33 ảnh và tệp');
    expect(text(root.querySelector('[data-person="x1"]')!)).toContain('Đã xóa');
  });

  it('offers to delete only a group nobody is in, and asks once more first', async () => {
    const a = api();
    mountStorage(root, a, vi.fn());
    await flush();
    expect(root.querySelector('[data-group="g1"] button')).toBeNull();
    const empty = root.querySelector('[data-group="g2"]')!;
    expect(text(empty)).toContain('Không còn ai');
    root.querySelector<HTMLButtonElement>('[data-group="g2"] [data-delete-group]')!.click();
    await flush();
    expect(a.deleteEmptyGroup).not.toHaveBeenCalled();
    root.querySelector<HTMLButtonElement>('[data-group="g2"] [data-cancel-delete]')!.click();
    await flush();
    expect(root.querySelector('[data-group="g2"] [data-confirm-delete]')).toBeNull();
    root.querySelector<HTMLButtonElement>('[data-group="g2"] [data-delete-group]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('[data-group="g2"] [data-confirm-delete]')!.click();
    await flush();
    expect(a.deleteEmptyGroup).toHaveBeenCalledWith('g2');
    expect(a.storage).toHaveBeenCalledTimes(2);
  });

  it('says why a delete was refused', async () => {
    mountStorage(root, api({ deleteEmptyGroup: vi.fn(async () => fail('group_not_empty', 409)) }), vi.fn());
    await flush();
    root.querySelector<HTMLButtonElement>('[data-group="g2"] [data-delete-group]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('[data-group="g2"] [data-confirm-delete]')!.click();
    await flush();
    expect(text(root)).toContain('Nhóm này vẫn còn thành viên.');
  });

  it('writes sizes up to gigabytes', () => {
    expect(formatSize(512)).toBe('512 B');
    expect(formatSize(3 * MB)).toBe('3 MB');
    expect(formatSize(1.5 * 1024 * MB)).toBe('1,5 GB');
  });
});
