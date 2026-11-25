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

  describe('trimming pictures and files', () => {
    const preview = { pictures: { count: 120, bytes: 400 * MB }, files: { count: 3, bytes: 10 * MB }, conversations: 5 };
    const set = (selector: string, value: string) => {
      const el = root.querySelector<HTMLInputElement | HTMLSelectElement>(selector)!;
      el.value = value;
      el.dispatchEvent(new Event(el instanceof HTMLSelectElement || el.type !== 'text' ? 'change' : 'input', { bubbles: true }));
    };
    const click = async (selector: string) => { root.querySelector<HTMLButtonElement>(selector)!.click(); await flush(); };

    it('previews the chosen rule, then trims only after Delete is typed exactly', async () => {
      const a = api({ trimPreview: vi.fn(async () => ok(preview)), trimFiles: vi.fn(async () => ok(preview)) });
      mountStorage(root, a, vi.fn());
      await flush();
      expect(root.querySelector<HTMLButtonElement>('[data-trim-preview]')!.disabled).toBe(true);   // no date yet
      set('#trim-day', '2026-01-01'); await flush();
      set('#trim-kind', 'image'); await flush();
      set('#trim-where', 'g1'); await flush();
      set('#trim-min', '1.5'); await flush();
      await click('[data-trim-preview]');

      const rule = { before: new Date('2026-01-01T00:00').toISOString(), kind: 'image', where: 'g1', minBytes: Math.round(1.5 * MB) };
      expect(a.trimPreview).toHaveBeenCalledWith(rule);
      expect(text(root.querySelector('[data-trim-summary]')!)).toBe('Sẽ xóa vĩnh viễn 120 ảnh (400 MB) và 3 tệp (10 MB) trong 5 cuộc trò chuyện.');

      const go = () => root.querySelector<HTMLButtonElement>('[data-trim-confirm]')!;
      expect(go().disabled).toBe(true);
      for (const typed of ['delete', 'Delete ', 'Xóa']) { set('#trim-confirm', typed); await flush(); expect(go().disabled).toBe(true); }
      set('#trim-confirm', 'Delete'); await flush();
      expect(go().disabled).toBe(false);
      await click('[data-trim-confirm]');
      expect(a.trimFiles).toHaveBeenCalledWith(rule, 'Delete');
      expect(text(root.querySelector('[data-trim-done]')!)).toBe('Đã xóa 120 ảnh (400 MB) và 3 tệp (10 MB) trong 5 cuộc trò chuyện.');
      expect(root.querySelector('[data-trim-confirm]')).toBeNull();
      expect(a.storage).toHaveBeenCalledTimes(2);
    });

    it('drops the preview when a choice changes, so what is confirmed is what was previewed', async () => {
      mountStorage(root, api({ trimPreview: vi.fn(async () => ok(preview)) }), vi.fn());
      await flush();
      set('#trim-day', '2026-01-01'); await flush();
      await click('[data-trim-preview]');
      set('#trim-confirm', 'Delete'); await flush();
      set('#trim-kind', 'file'); await flush();
      expect(root.querySelector('[data-trim-summary]')).toBeNull();
      expect(root.querySelector('[data-trim-confirm]')).toBeNull();
    });

    it('says so when nothing matches, and offers no delete', async () => {
      const none = { pictures: { count: 0, bytes: 0 }, files: { count: 0, bytes: 0 }, conversations: 0 };
      mountStorage(root, api({ trimPreview: vi.fn(async () => ok(none)) }), vi.fn());
      await flush();
      set('#trim-day', '2026-01-01'); await flush();
      await click('[data-trim-preview]');
      expect(text(root.querySelector('[data-trim-none]')!)).toBe('Không có ảnh hay tệp nào khớp.');
      expect(root.querySelector('[data-trim-confirm]')).toBeNull();
    });

    it('offers every group, and one-to-one chats only together', async () => {
      mountStorage(root, api(), vi.fn());
      await flush();
      const options = [...root.querySelectorAll<HTMLOptionElement>('#trim-where option')].map((o) => [o.value, text(o)]);
      expect(options).toEqual([['all', 'Mọi cuộc trò chuyện'], ['direct', 'Mọi trò chuyện 1-1'], ['g1', 'Nhóm: Nhà mình'], ['g2', 'Nhóm: Bỏ trống']]);
    });
  });

  describe('trimming messages', () => {
    const result = { messages: 347, pictures: { count: 120, bytes: 400 * MB }, files: { count: 3, bytes: 10 * MB }, conversations: 5 };
    const set = (selector: string, value: string) => {
      const el = root.querySelector<HTMLInputElement | HTMLSelectElement>(selector)!;
      el.value = value;
      el.dispatchEvent(new Event(el instanceof HTMLSelectElement || el.type !== 'text' ? 'change' : 'input', { bubbles: true }));
    };
    const click = async (selector: string) => { root.querySelector<HTMLButtonElement>(selector)!.click(); await flush(); };

    it('previews, then trims only after Delete is typed exactly, and says what went', async () => {
      const a = api({ trimMessagesPreview: vi.fn(async () => ok(result)), trimMessages: vi.fn(async () => ok(result)) });
      mountStorage(root, a, vi.fn());
      await flush();
      expect(root.querySelector<HTMLButtonElement>('[data-mtrim-preview]')!.disabled).toBe(true);
      set('#mtrim-day', '2026-01-01'); await flush();
      set('#mtrim-where', 'direct'); await flush();
      await click('[data-mtrim-preview]');
      const rule = { before: new Date('2026-01-01T00:00').toISOString(), where: 'direct' };
      expect(a.trimMessagesPreview).toHaveBeenCalledWith(rule);
      expect(text(root.querySelector('[data-mtrim-summary]')!)).toBe('Sẽ xóa vĩnh viễn 347 tin nhắn, kèm 120 ảnh (400 MB) và 3 tệp (10 MB), trong 5 cuộc trò chuyện.');
      const go = () => root.querySelector<HTMLButtonElement>('[data-mtrim-confirm]')!;
      set('#mtrim-confirm', 'delete'); await flush();
      expect(go().disabled).toBe(true);
      set('#mtrim-confirm', 'Delete'); await flush();
      await click('[data-mtrim-confirm]');
      expect(a.trimMessages).toHaveBeenCalledWith(rule, 'Delete');
      expect(text(root.querySelector('[data-mtrim-done]')!)).toBe('Đã xóa 347 tin nhắn, kèm 120 ảnh (400 MB) và 3 tệp (10 MB), trong 5 cuộc trò chuyện.');
      expect(a.trimFiles).not.toHaveBeenCalled();
    });

    it('drops the preview when a choice changes, and says so when nothing matches', async () => {
      const none = { messages: 0, pictures: { count: 0, bytes: 0 }, files: { count: 0, bytes: 0 }, conversations: 0 };
      mountStorage(root, api({ trimMessagesPreview: vi.fn(async () => ok(none)) }), vi.fn());
      await flush();
      set('#mtrim-day', '2026-01-01'); await flush();
      await click('[data-mtrim-preview]');
      expect(text(root.querySelector('[data-mtrim-none]')!)).toBe('Không có tin nhắn nào khớp.');
      set('#mtrim-where', 'g1'); await flush();
      expect(root.querySelector('[data-mtrim-none]')).toBeNull();
    });
  });

  it('writes sizes up to gigabytes', () => {
    expect(formatSize(512)).toBe('512 B');
    expect(formatSize(3 * MB)).toBe('3 MB');
    expect(formatSize(1.5 * 1024 * MB)).toBe('1,5 GB');
  });
});
