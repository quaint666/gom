import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mountOwner } from '../Scripts/pages/owner.js';
import { fail, fakeApi, flush, ok, text } from './helpers.js';

let root: HTMLElement;
beforeEach(() => { root = document.createElement('div'); document.body.replaceChildren(root); });

const owner = { gomId: 'boss', displayName: 'Boss', isOwner: true };
const invites = [
  { id: 'i1', createdAt: '2026-12-01T09:00:00Z', expiresAt: '2026-12-08T09:00:00Z', uses: 1, maxUses: 10, status: 'active', members: [{ gomId: 'lan', displayName: 'Lan', usedAt: '2026-12-02T09:00:00Z' }] },
  { id: 'i2', createdAt: '2026-11-25T09:00:00Z', expiresAt: '2026-12-02T09:00:00Z', uses: 1, maxUses: 1, status: 'full', members: [{ gomId: 'minh', displayName: 'Minh', usedAt: '2026-11-26T09:00:00Z' }] },
];
const members = [
  { gomId: 'boss', displayName: 'Boss', isOwner: true, disabled: false, createdAt: '2026-11-01T00:00:00Z' },
  { gomId: 'minh', displayName: 'Minh', isOwner: false, disabled: false, createdAt: '2026-11-25T00:00:00Z' },
];

function api(extra = {}) {
  return fakeApi({ me: vi.fn(async () => ok(owner)), invites: vi.fn(async () => ok(invites)), members: vi.fn(async () => ok(members)), ...extra });
}

describe('owner page', () => {
  it('sends a member away', async () => {
    const go = vi.fn();
    mountOwner(root, fakeApi({ me: vi.fn(async () => ok({ ...owner, isOwner: false })) }), go, 'https://gom.test', 'invites');
    await flush();
    expect(go).toHaveBeenCalledWith('/');
  });

  it('lists invites with a status in words, and revokes only pending ones', async () => {
    mountOwner(root, api(), vi.fn(), 'https://gom.test', 'invites');
    await flush();
    const pending = root.querySelector('[data-invite="i1"]')!;
    const used = root.querySelector('[data-invite="i2"]')!;
    expect(text(pending)).toContain('Đang hoạt động');
    expect(text(pending)).toContain('1 / 10 người');
    expect(text(pending.querySelector('[data-joined]')!)).toContain('Lan (@lan)');
    expect(pending.querySelector('button[data-row-more]')!.getAttribute('aria-label')).toBe('Tùy chọn lời mời');
    expect(text(used)).toContain('Đã đủ người');
    expect(text(used)).toContain('minh');
    expect(used.querySelector('button')).toBeNull();
  });

  it('revokes an invite and reloads', async () => {
    const a = api();
    mountOwner(root, a, vi.fn(), 'https://gom.test', 'invites');
    await flush();
    root.querySelector<HTMLButtonElement>('[data-invite="i1"] button[data-row-more]')!.click();
    await flush();
    const menu = root.querySelector<HTMLElement>('[data-row-actions]')!;
    expect([...menu.querySelectorAll('[role="menuitem"]')].map((b) => text(b))).toEqual(['Thu hồi lời mời']);
    menu.querySelector<HTMLButtonElement>('button[data-revoke]')!.click();
    await flush();
    expect(a.revokeInvite).toHaveBeenCalledWith('i1');
    expect(root.querySelector('[data-row-actions]')).toBeNull();
    expect(a.invites).toHaveBeenCalledTimes(2);
  });

  it('creates an invite and shows the full link once, readable and copyable', async () => {
    const a = api({ createInvite: vi.fn(async () => ok({ id: 'i3', token: 'TOK', path: '/join/TOK', expiresAt: '2026-12-08T09:00:00Z' })) });
    mountOwner(root, a, vi.fn(), 'https://gom.test', 'invites');
    await flush();
    root.querySelector<HTMLButtonElement>('button[data-create-invite]')!.click();
    await flush();
    const field = root.querySelector<HTMLInputElement>('input[data-link]')!;
    expect(field.value).toBe('https://gom.test/join/TOK');
    expect(field.readOnly).toBe(true);
    expect(a.createInvite).toHaveBeenCalledWith({ days: 7, uses: 10 });   // the defaults
  });

  it('creates the invite with the days and the number of people that were chosen, and says so', async () => {
    const a = api({ createInvite: vi.fn(async () => ok({ id: 'i3', token: 'TOK', path: '/join/TOK', expiresAt: '2026-12-31T09:00:00Z' })) });
    mountOwner(root, a, vi.fn(), 'https://gom.test', 'invites');
    await flush();
    const choose = (name: string, value: string) => { const sel = root.querySelector<HTMLSelectElement>(`select[name="${name}"]`)!; sel.value = value; sel.dispatchEvent(new Event('change', { bubbles: true })); };
    choose('days', '30'); choose('uses', '0');
    root.querySelector<HTMLButtonElement>('button[data-create-invite]')!.click();
    await flush();
    expect(a.createInvite).toHaveBeenCalledWith({ days: 30, uses: 0 });
    expect(text(root.querySelector('.card .hint')!)).toContain('30 ngày');
    expect(text(root.querySelector('.card .hint')!)).toContain('bất kỳ ai');
    choose('uses', '1');
    root.querySelector<HTMLButtonElement>('button[data-create-invite]')!.click();
    await flush();
    expect(a.createInvite).toHaveBeenLastCalledWith({ days: 30, uses: 1 });
    expect(text(root.querySelector('.card .hint')!)).toContain('một người');
  });

  it('offers 1, 7 and 30 days and 1, 10 or any number of people, with 7 days and 10 people chosen', async () => {
    mountOwner(root, api(), vi.fn(), 'https://gom.test', 'invites');
    await flush();
    const days = root.querySelector<HTMLSelectElement>('select[name="days"]')!;
    const uses = root.querySelector<HTMLSelectElement>('select[name="uses"]')!;
    expect([...days.options].map((o) => o.value)).toEqual(['1', '7', '30']);
    expect([...uses.options].map((o) => o.value)).toEqual(['1', '10', '0']);
    expect(days.value).toBe('7');
    expect(uses.value).toBe('10');
  });

  it('copies the link and says so, or says it failed', async () => {
    const writeText = vi.fn(async () => undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const a = api({ createInvite: vi.fn(async () => ok({ id: 'i3', token: 'TOK', path: '/join/TOK', expiresAt: '2026-12-08T09:00:00Z' })) });
    mountOwner(root, a, vi.fn(), 'https://gom.test', 'invites');
    await flush();
    root.querySelector<HTMLButtonElement>('button[data-create-invite]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('button[data-copy]')!.click();
    await flush();
    expect(writeText).toHaveBeenCalledWith('https://gom.test/join/TOK');
    expect(text(root.querySelector('[role="status"]')!)).toContain('Đã sao chép');

    writeText.mockRejectedValueOnce(new Error('denied'));
    root.querySelector<HTMLButtonElement>('button[data-copy]')!.click();
    await flush();
    expect(text(root.querySelector('[role="status"]')!)).toContain('Không sao chép được');
  });

  it('makes a reset link for a member, not for the owner themself', async () => {
    const a = api({ resetLink: vi.fn(async () => ok({ token: 'RT', path: '/reset/RT', expiresAt: '2026-12-02T09:00:00Z' })) });
    mountOwner(root, a, vi.fn(), 'https://gom.test', 'members');
    await flush();
    expect(root.querySelector('[data-member="boss"] button')).toBeNull();
    root.querySelector<HTMLButtonElement>('[data-member="minh"] button[data-row-more]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('[data-row-actions] button[data-reset]')!.click();
    await flush();
    expect(a.resetLink).toHaveBeenCalledWith('minh');
    expect(root.querySelector<HTMLInputElement>('input[data-link]')!.value).toBe('https://gom.test/reset/RT');
    expect(text(root)).toContain('24 giờ');
  });

  it('shows a failure in words', async () => {
    const a = api({ createInvite: vi.fn(async () => fail('server', 500)) });
    mountOwner(root, a, vi.fn(), 'https://gom.test', 'invites');
    await flush();
    root.querySelector<HTMLButtonElement>('button[data-create-invite]')!.click();
    await flush();
    expect(root.querySelector('[role="alert"]')).not.toBeNull();
  });

  const crowd = Array.from({ length: 9 }, (_, n) => ({ gomId: `u${n}`, displayName: n === 7 ? 'Đào Mai' : `Người ${n}`, isOwner: false, disabled: false, createdAt: '2026-11-25T00:00:00Z' }));

  it('lists every member on its own page, and narrows to the locked ones', async () => {
    const people = crowd.map((m, n) => ({ ...m, disabled: n === 2 }));
    mountOwner(root, api({ members: vi.fn(async () => ok(people)) }), vi.fn(), 'https://gom.test', 'members');
    await flush();
    expect(root.querySelectorAll('[data-member]').length).toBe(9);
    expect(text(root.querySelector('[data-member-filter="locked"]')!)).toContain('1');
    root.querySelector<HTMLButtonElement>('[data-member-filter="locked"]')!.click();
    await flush();
    expect([...root.querySelectorAll('[data-member]')].map((n) => n.getAttribute('data-member'))).toEqual(['u2']);
  });

  it('finds a member by name without accents, and says when nobody matches', async () => {
    mountOwner(root, api({ members: vi.fn(async () => ok(crowd)) }), vi.fn(), 'https://gom.test', 'members');
    await flush();
    const input = root.querySelector<HTMLInputElement>('input[name="member-search"]')!;
    input.value = 'dao mai';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    await flush();
    expect([...root.querySelectorAll('[data-member]')].map((n) => n.getAttribute('data-member'))).toEqual(['u7']);
    input.value = 'zzz';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    await flush();
    expect(root.querySelector('[data-no-member]')).not.toBeNull();
  });

  it('always has a search, even for a few members', async () => {
    mountOwner(root, api(), vi.fn(), 'https://gom.test', 'members');
    await flush();
    expect(root.querySelector('input[name="member-search"]')).not.toBeNull();
  });

  it('shows the working links, and the past ones folded under their count', async () => {
    const many = Array.from({ length: 8 }, (_, n) => ({ id: `x${n}`, createdAt: '2026-12-01T09:00:00Z', expiresAt: '2026-12-08T09:00:00Z', uses: 0, maxUses: 10, status: n < 3 ? 'active' : 'expired', members: [] }));
    mountOwner(root, api({ invites: vi.fn(async () => ok(many)) }), vi.fn(), 'https://gom.test', 'invites');
    await flush();
    expect(root.querySelectorAll('[data-active-invites] [data-invite]').length).toBe(3);
    const past = root.querySelector<HTMLDetailsElement>('details[data-fold="past"]')!;
    expect(past.open).toBe(false);
    expect(text(past.querySelector('summary')!)).toContain('Liên kết cũ (5)');
    expect(past.querySelectorAll('[data-invite]').length).toBe(5);
  });

  it('says so when no link is working', async () => {
    mountOwner(root, api({ invites: vi.fn(async () => ok([])) }), vi.fn(), 'https://gom.test', 'invites');
    await flush();
    expect(root.querySelector('[data-no-active]')).not.toBeNull();
    expect(root.querySelector('details[data-fold="past"]')).toBeNull();
  });
});

describe('contact messages', () => {
  const note = { id: 'c1', body: 'Xin xóa ảnh\ncủa tôi', replyTo: 'an@example.com', createdAt: '2026-12-01T09:00:00Z' };

  it('lists what was written from the privacy page, with how to answer and how long it is kept', async () => {
    mountOwner(root, api({ contactMessages: vi.fn(async () => ok({ messages: [note, { ...note, id: 'c2', replyTo: null }] })) }), vi.fn(), 'https://gom.test', 'contact');
    await flush();
    expect(text(root)).toContain('Thư tự xóa sau 180 ngày');
    expect(text(root.querySelector('[data-contact="c1"]')!)).toContain('liên lạc: an@example.com');
    expect(root.querySelector('[data-contact="c1"] .request-note')?.textContent).toBe('Xin xóa ảnh\ncủa tôi');
    expect(text(root.querySelector('[data-contact="c2"]')!)).not.toContain('liên lạc');
  });

  it('deletes one and asks again', async () => {
    const list = vi.fn(async () => ok({ messages: [note] }));
    const a = api({ contactMessages: list });
    mountOwner(root, a, vi.fn(), 'https://gom.test', 'contact');
    await flush();
    root.querySelector<HTMLButtonElement>('[data-contact="c1"] [data-delete-contact]')!.click();
    await flush();
    expect(a.deleteContact).toHaveBeenCalledWith('c1');
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('says when there are none', async () => {
    mountOwner(root, api(), vi.fn(), 'https://gom.test', 'contact');
    await flush();
    expect(root.querySelector('[data-no-contact]')).not.toBeNull();
  });
});

describe('owner page: sticker packs', () => {
  const code = 'c-0f8fad5bd9cb469fa16570867728950e';
  const pack = (stickers = [{ id: 's1', code }]) => ({ id: 'p1', name: 'Mèo nhà', stickers });

  it('makes a pack, adds the pictures picked one by one, and lists it with its count', async () => {
    let packs: ReturnType<typeof pack>[] = [];
    const createStickerPack = vi.fn(async (name: string) => { packs = [{ ...pack([]), name }]; return ok(packs[0]); });
    const addSticker = vi.fn(async () => { packs = [pack()]; return ok({ id: 's1', code }); });
    mountOwner(root, api({ stickerPacks: vi.fn(async () => ok(packs)), createStickerPack, addSticker }), vi.fn(), 'https://gom.test', 'stickers');
    await flush();
    const name = root.querySelector<HTMLInputElement>('#pack-name')!;
    name.value = 'Mèo nhà'; name.dispatchEvent(new Event('input', { bubbles: true }));
    root.querySelector('[data-new-pack]')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await flush();
    expect(createStickerPack).toHaveBeenCalledWith('Mèo nhà');
    expect(text(root.querySelector('[data-pack="p1"]')!)).toContain('0/100 nhãn dán');
    const input = root.querySelector<HTMLInputElement>('[data-pack="p1"] input[type="file"]')!;
    const files = [new File([new Uint8Array([1])], 'a.png', { type: 'image/png' }), new File([new Uint8Array([2])], 'b.png', { type: 'image/png' })];
    Object.defineProperty(input, 'files', { value: files, configurable: true });
    input.dispatchEvent(new Event('change', { bubbles: true }));
    await flush(); await flush();
    expect(addSticker.mock.calls.map((c) => (c as unknown[])[1])).toEqual(files);
    expect(root.querySelector<HTMLImageElement>('[data-pack-sticker="s1"] img')!.getAttribute('src')).toBe('/api/stickers/0f8fad5bd9cb469fa16570867728950e');
    expect(text(root.querySelector('[data-pack="p1"]')!)).toContain('1/100 nhãn dán');
  });

  it('says why a picture was turned away', async () => {
    mountOwner(root, api({ stickerPacks: vi.fn(async () => ok([pack()])), addSticker: vi.fn(async () => fail('pack_full', 400)) }), vi.fn(), 'https://gom.test', 'stickers');
    await flush();
    const input = root.querySelector<HTMLInputElement>('[data-pack="p1"] input[type="file"]')!;
    Object.defineProperty(input, 'files', { value: [new File([new Uint8Array([1])], 'a.png')], configurable: true });
    input.dispatchEvent(new Event('change', { bubbles: true }));
    await flush(); await flush();
    expect(text(root.querySelector('[role="alert"]')!)).toContain('Bộ này đã đủ 100 nhãn dán.');
  });

  it('takes a sticker out, and deletes a pack only after asking', async () => {
    const deleteSticker = vi.fn(async () => ok(undefined));
    const deleteStickerPack = vi.fn(async () => ok(undefined));
    mountOwner(root, api({ stickerPacks: vi.fn(async () => ok([pack()])), deleteSticker, deleteStickerPack }), vi.fn(), 'https://gom.test', 'stickers');
    await flush();
    root.querySelector<HTMLButtonElement>('[data-pack-sticker="s1"] [data-remove-sticker]')!.click();
    await flush();
    expect(deleteSticker).toHaveBeenCalledWith('s1');
    root.querySelector<HTMLButtonElement>('[data-delete-pack]')!.click();
    await flush();
    expect(deleteStickerPack).not.toHaveBeenCalled();
    expect(text(root.querySelector('[data-delete-pack-ask]')!)).toContain('nhãn dán đã gửi vẫn hiện');
    root.querySelector<HTMLButtonElement>('[data-delete-pack-yes]')!.click();
    await flush();
    expect(deleteStickerPack).toHaveBeenCalledWith('p1');
  });
});
