import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mountOwner } from '../Scripts/pages/owner.js';
import { fail, fakeApi, flush, ok, text } from './helpers.js';

let root: HTMLElement;
beforeEach(() => { root = document.createElement('div'); document.body.replaceChildren(root); });

const owner = { gomId: 'boss', displayName: 'Boss', isOwner: true };
const invites = [
  { id: 'i1', createdAt: '2026-12-01T09:00:00Z', expiresAt: '2026-12-08T09:00:00Z', usedBy: null, status: 'pending' },
  { id: 'i2', createdAt: '2026-11-25T09:00:00Z', expiresAt: '2026-12-02T09:00:00Z', usedBy: 'minh', status: 'used' },
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
    mountOwner(root, fakeApi({ me: vi.fn(async () => ok({ ...owner, isOwner: false })) }), go, 'https://gom.test');
    await flush();
    expect(go).toHaveBeenCalledWith('/');
  });

  it('lists invites with a status in words, and revokes only pending ones', async () => {
    mountOwner(root, api(), vi.fn(), 'https://gom.test');
    await flush();
    const pending = root.querySelector('[data-invite="i1"]')!;
    const used = root.querySelector('[data-invite="i2"]')!;
    expect(text(pending)).toContain('Đang chờ');
    expect(pending.querySelector('button')).not.toBeNull();
    expect(text(used)).toContain('minh');
    expect(used.querySelector('button')).toBeNull();
  });

  it('revokes an invite and reloads', async () => {
    const a = api();
    mountOwner(root, a, vi.fn(), 'https://gom.test');
    await flush();
    root.querySelector<HTMLButtonElement>('[data-invite="i1"] button')!.click();
    await flush();
    expect(a.revokeInvite).toHaveBeenCalledWith('i1');
    expect(a.invites).toHaveBeenCalledTimes(2);
  });

  it('creates an invite and shows the full link once, readable and copyable', async () => {
    const a = api({ createInvite: vi.fn(async () => ok({ id: 'i3', token: 'TOK', path: '/join/TOK', expiresAt: '2026-12-08T09:00:00Z' })) });
    mountOwner(root, a, vi.fn(), 'https://gom.test');
    await flush();
    root.querySelector<HTMLButtonElement>('button[data-create-invite]')!.click();
    await flush();
    const field = root.querySelector<HTMLInputElement>('input[data-link]')!;
    expect(field.value).toBe('https://gom.test/join/TOK');
    expect(field.readOnly).toBe(true);
  });

  it('copies the link and says so, or says it failed', async () => {
    const writeText = vi.fn(async () => undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const a = api({ createInvite: vi.fn(async () => ok({ id: 'i3', token: 'TOK', path: '/join/TOK', expiresAt: '2026-12-08T09:00:00Z' })) });
    mountOwner(root, a, vi.fn(), 'https://gom.test');
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
    mountOwner(root, a, vi.fn(), 'https://gom.test');
    await flush();
    expect(root.querySelector('[data-member="boss"] button')).toBeNull();
    root.querySelector<HTMLButtonElement>('[data-member="minh"] button')!.click();
    await flush();
    expect(a.resetLink).toHaveBeenCalledWith('minh');
    expect(root.querySelector<HTMLInputElement>('input[data-link]')!.value).toBe('https://gom.test/reset/RT');
    expect(text(root)).toContain('24 giờ');
  });

  it('shows a failure in words', async () => {
    const a = api({ createInvite: vi.fn(async () => fail('server', 500)) });
    mountOwner(root, a, vi.fn(), 'https://gom.test');
    await flush();
    root.querySelector<HTMLButtonElement>('button[data-create-invite]')!.click();
    await flush();
    expect(root.querySelector('[role="alert"]')).not.toBeNull();
  });

  const crowd = Array.from({ length: 9 }, (_, n) => ({ gomId: `u${n}`, displayName: n === 7 ? 'Đào Mai' : `Người ${n}`, isOwner: false, disabled: false, createdAt: '2026-11-25T00:00:00Z' }));

  it('shows a handful of members, the rest behind "Xem thêm"', async () => {
    mountOwner(root, api({ members: vi.fn(async () => ok(crowd)) }), vi.fn(), 'https://gom.test');
    await flush();
    expect(root.querySelectorAll('[data-member]').length).toBe(5);
    root.querySelector<HTMLButtonElement>('[data-more-members]')!.click();
    await flush();
    expect(root.querySelectorAll('[data-member]').length).toBe(9);
    expect(root.querySelector('[data-more-members]')).toBeNull();
  });

  it('finds a member by name without accents, and says when nobody matches', async () => {
    mountOwner(root, api({ members: vi.fn(async () => ok(crowd)) }), vi.fn(), 'https://gom.test');
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

  it('has no search when there are only a few members', async () => {
    mountOwner(root, api(), vi.fn(), 'https://gom.test');
    await flush();
    expect(root.querySelector('input[name="member-search"]')).toBeNull();
  });

  it('shows a handful of invites too', async () => {
    const many = Array.from({ length: 8 }, (_, n) => ({ id: `x${n}`, createdAt: '2026-12-01T09:00:00Z', expiresAt: '2026-12-08T09:00:00Z', usedBy: null, status: 'pending' }));
    mountOwner(root, api({ invites: vi.fn(async () => ok(many)) }), vi.fn(), 'https://gom.test');
    await flush();
    expect(root.querySelectorAll('[data-invite]').length).toBe(5);
    root.querySelector<HTMLButtonElement>('[data-more-invites]')!.click();
    await flush();
    expect(root.querySelectorAll('[data-invite]').length).toBe(8);
  });
});
