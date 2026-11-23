import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mountJoin } from '../Scripts/pages/join.js';
import { mountReset } from '../Scripts/pages/reset.js';
import { fail, fakeApi, fill, flush, ok, submit, text } from './helpers.js';

let root: HTMLElement;
beforeEach(() => { root = document.createElement('div'); document.body.replaceChildren(root); });

describe('join page', () => {
  it('shows the "invite no longer works" screen for a dead link, with a way to sign in', async () => {
    const api = fakeApi({ peekInvite: vi.fn(async () => fail('invalid_link', 404)) });
    mountJoin(root, api, vi.fn(), 'dead-token');
    await flush();
    expect(text(root)).toContain('Lời mời không dùng được nữa');
    expect(root.querySelector('a[href="/sign-in"]')).not.toBeNull();
    expect(root.querySelector('form')).toBeNull();
  });

  it('says who invited you and has a sign-up form with the right autofill hints', async () => {
    const api = fakeApi({ peekInvite: vi.fn(async () => ok({ invitedBy: 'Mẹ' })) });
    mountJoin(root, api, vi.fn(), 'tok');
    await flush();
    expect(text(root)).toContain('Mẹ');
    expect(root.querySelector<HTMLInputElement>('input[name="gomId"]')!.autocomplete).toBe('username');
    expect(root.querySelector<HTMLInputElement>('input[name="password"]')!.autocomplete).toBe('new-password');
    expect(root.querySelector<HTMLInputElement>('input[name="displayName"]')!.required).toBe(false);
    expect(root.querySelector('button[type="submit"]')!.textContent).toContain('Tạo tài khoản');
  });

  it('creates the account and goes to the home page', async () => {
    const api = fakeApi({
      peekInvite: vi.fn(async () => ok({ invitedBy: 'Mẹ' })),
      join: vi.fn(async () => ok({ gomId: 'minh_tam', displayName: 'Minh Tâm', isOwner: false })),
    });
    const go = vi.fn();
    mountJoin(root, api, go, 'tok');
    await flush();
    fill(root, 'input[name="displayName"]', 'Minh Tâm');
    fill(root, 'input[name="gomId"]', 'minh_tam');
    fill(root, 'input[name="password"]', 'a long enough password');
    submit(root);
    await flush();
    expect(api.join).toHaveBeenCalledWith('tok', { gomId: 'minh_tam', password: 'a long enough password', displayName: 'Minh Tâm' });
    expect(go).toHaveBeenCalledWith('/');
  });

  it('shows the reason in words and keeps the form when the ID is taken', async () => {
    const api = fakeApi({ peekInvite: vi.fn(async () => ok({ invitedBy: 'Mẹ' })), join: vi.fn(async () => fail('id_taken', 409)) });
    mountJoin(root, api, vi.fn(), 'tok');
    await flush();
    fill(root, 'input[name="gomId"]', 'taken_one');
    fill(root, 'input[name="password"]', 'a long enough password');
    submit(root);
    await flush();
    expect(text(root.querySelector('[role="alert"]')!)).toContain('đã có người dùng');
    expect(root.querySelector<HTMLInputElement>('input[name="gomId"]')!.value).toBe('taken_one');
  });

  it('the invite can turn out dead only when you submit: then it shows the dead-link screen', async () => {
    const api = fakeApi({ peekInvite: vi.fn(async () => ok({ invitedBy: 'Mẹ' })), join: vi.fn(async () => fail('invalid_link', 404)) });
    mountJoin(root, api, vi.fn(), 'tok');
    await flush();
    fill(root, 'input[name="gomId"]', 'someone');
    fill(root, 'input[name="password"]', 'a long enough password');
    submit(root);
    await flush();
    expect(text(root)).toContain('Lời mời không dùng được nữa');
  });
});

describe('reset page', () => {
  it('names whose password is being reset, then saves and goes home', async () => {
    const api = fakeApi({
      peekReset: vi.fn(async () => ok({ gomId: 'me_yeu', displayName: 'Mẹ' })),
      reset: vi.fn(async () => ok({ gomId: 'me_yeu', displayName: 'Mẹ', isOwner: false })),
    });
    const go = vi.fn();
    mountReset(root, api, go, 'tok');
    await flush();
    expect(text(root)).toContain('@me_yeu');
    expect(root.querySelector<HTMLInputElement>('input[name="password"]')!.autocomplete).toBe('new-password');
    // Password managers want to know which account the new password belongs to.
    const who = root.querySelector<HTMLInputElement>('input[autocomplete="username"]')!;
    expect(who.value).toBe('me_yeu');
    expect(who.readOnly).toBe(true);
    fill(root, 'input[name="password"]', 'a brand new password');
    submit(root);
    await flush();
    expect(api.reset).toHaveBeenCalledWith('tok', 'a brand new password');
    expect(go).toHaveBeenCalledWith('/');
  });

  it('shows a dead-link screen when the link is no good', async () => {
    mountReset(root, fakeApi({ peekReset: vi.fn(async () => fail('invalid_link', 404)) }), vi.fn(), 'tok');
    await flush();
    expect(text(root)).toContain('Liên kết không dùng được nữa');
  });

  it('shows why a weak password was refused', async () => {
    const api = fakeApi({ peekReset: vi.fn(async () => ok({ gomId: 'a', displayName: 'A' })), reset: vi.fn(async () => fail('password_too_short')) });
    mountReset(root, api, vi.fn(), 'tok');
    await flush();
    fill(root, 'input[name="password"]', 'short');
    submit(root);
    await flush();
    expect(text(root.querySelector('[role="alert"]')!)).toContain('10 ký tự');
  });
});
