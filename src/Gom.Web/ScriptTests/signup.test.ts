import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createApi } from '../Scripts/api.js';
import { mountSignIn } from '../Scripts/pages/signin.js';
import { mountSignUp } from '../Scripts/pages/signup.js';
import { mountWaiting } from '../Scripts/pages/waiting.js';
import { mountOwner } from '../Scripts/pages/owner.js';
import { fail, fakeApi, fill, flush, ok, submit, text } from './helpers.js';

let root: HTMLElement;
beforeEach(() => { root = document.createElement('div'); document.body.replaceChildren(root); });

describe('sign-in: asking to join', () => {
  it('offers to ask to join only when the owner has opened it', async () => {
    mountSignIn(root, fakeApi({ signUpOpen: vi.fn(async () => ok({ open: true })) }), vi.fn());
    await flush();
    expect(root.querySelector<HTMLAnchorElement>('[data-ask-to-join]')!.getAttribute('href')).toBe('/sign-up');

    root = document.createElement('div'); document.body.replaceChildren(root);
    mountSignIn(root, fakeApi(), vi.fn());
    await flush();
    expect(root.querySelector('[data-ask-to-join]')).toBeNull();
    expect(text(root.querySelector('[data-sign-in-foot]')!)).toContain('chỉ dành cho người được mời');
  });
});

describe('sign-up page', () => {
  const open = (extra = {}) => fakeApi({ signUpOpen: vi.fn(async () => ok({ open: true })), ...extra });

  it('says it is closed when the owner has not opened it', async () => {
    mountSignUp(root, fakeApi(), vi.fn());
    await flush();
    expect(root.querySelector('[data-sign-up-closed]')).not.toBeNull();
    expect(root.querySelector('form')).toBeNull();
  });

  it('sends the form with who the person is, then goes home to wait', async () => {
    const signUp = vi.fn(async () => ok({ id: 'u1', gomId: 'bi', displayName: 'Bi', isOwner: false, standing: 'pending' }));
    const go = vi.fn();
    mountSignUp(root, open({ signUp }), go);
    await flush();
    expect(root.querySelector('textarea[name="note"]')!.getAttribute('maxlength')).toBe('200');
    fill(root, 'input[name="displayName"]', 'Bi');
    fill(root, 'input[name="gomId"]', 'bi');
    fill(root, 'input[name="password"]', 'correct horse battery');
    fill(root, 'textarea[name="note"]', 'Con của cô Lan');
    submit(root);
    await flush();
    expect(signUp).toHaveBeenCalledWith({ gomId: 'bi', password: 'correct horse battery', displayName: 'Bi', note: 'Con của cô Lan' });
    expect(go).toHaveBeenCalledWith('/');
  });

  it('shows why a request was refused, and turns to "closed" if the owner closed it meanwhile', async () => {
    const signUp = vi.fn().mockResolvedValueOnce(fail('invalid_note')).mockResolvedValueOnce(fail('sign_up_closed', 403));
    mountSignUp(root, open({ signUp }), vi.fn());
    await flush();
    submit(root);
    await flush();
    expect(text(root)).toContain('Hãy cho chủ Gom biết bạn là ai (tối đa 200 ký tự).');
    submit(root);
    await flush();
    expect(root.querySelector('[data-sign-up-closed]')).not.toBeNull();
  });
});

describe('the waiting screen', () => {
  const pending = { id: 'u1', gomId: 'bi', displayName: 'Bi', isOwner: false, standing: 'pending' as const };

  it('checks again on a tap: still waiting says so, approved goes into Gom', async () => {
    const me = vi.fn().mockResolvedValueOnce(ok(pending)).mockResolvedValueOnce(ok({ ...pending, standing: 'member' }));
    const go = vi.fn();
    mountWaiting(root, fakeApi({ me }), go, pending, () => undefined);
    expect(root.querySelector('[data-waiting="pending"]')).not.toBeNull();
    root.querySelector<HTMLButtonElement>('[data-check]')!.click();
    await flush();
    expect(text(root.querySelector('[data-waiting-status]')!)).toBe('Vẫn đang chờ duyệt.');
    root.querySelector<HTMLButtonElement>('[data-check]')!.click();
    await flush();
    expect(go).toHaveBeenCalledWith('/');
  });

  it('checks again by itself when the person comes back to the page', async () => {
    let comeBack = (): void => undefined;
    const me = vi.fn(async () => ok({ ...pending, standing: 'refused' }));
    mountWaiting(root, fakeApi({ me }), vi.fn(), pending, (check) => { comeBack = check; });
    comeBack();
    await flush();
    expect(root.querySelector('[data-waiting="refused"]')).not.toBeNull();
  });

  it('refused: the request can be taken back after a question, which leaves for sign-in', async () => {
    const go = vi.fn();
    const a = fakeApi();
    mountWaiting(root, a, go, { ...pending, standing: 'refused' }, () => undefined);
    expect(text(root)).toContain('chưa được duyệt');
    root.querySelector<HTMLButtonElement>('[data-withdraw]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('[data-withdraw-no]')!.click();
    await flush();
    expect(a.withdrawSignUp).not.toHaveBeenCalled();
    root.querySelector<HTMLButtonElement>('[data-withdraw]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('[data-withdraw-yes]')!.click();
    await flush();
    expect(a.withdrawSignUp).toHaveBeenCalledOnce();
    expect(go).toHaveBeenCalledWith('/sign-in');
  });

  it('signs out from either state', async () => {
    const go = vi.fn();
    const a = fakeApi({ signOut: vi.fn(async () => ok(undefined)) });
    mountWaiting(root, a, go, pending, () => undefined);
    root.querySelector<HTMLButtonElement>('[data-signout]')!.click();
    await flush();
    expect(a.signOut).toHaveBeenCalledOnce();
    expect(go).toHaveBeenCalledWith('/sign-in');
  });
});

describe('the owner: requests to join', () => {
  const owner = { id: 'u0', gomId: 'boss', displayName: 'Boss', isOwner: true };
  const requests = [
    { gomId: 'bi', displayName: 'Bi', note: 'Con của cô Lan', requestedAt: '2026-12-01T09:00:00Z', refused: false },
    { gomId: 'spam', displayName: 'Spam', note: 'xin chào', requestedAt: '2026-11-30T09:00:00Z', refused: true },
  ];
  const api = (extra = {}) => fakeApi({
    me: vi.fn(async () => ok(owner)), invites: vi.fn(async () => ok([])), members: vi.fn(async () => ok([])),
    signUpRequests: vi.fn(async () => ok({ open: true, requests })), ...extra,
  });

  it('lists who is waiting with what they said, and the refused ones apart', async () => {
    mountOwner(root, api(), vi.fn(), 'https://gom.test', 'requests');
    await flush(); await flush();
    expect(root.querySelector<HTMLInputElement>('[data-sign-up-open]')!.checked).toBe(true);
    expect(text(root)).toContain('Chờ duyệt (1)');
    expect(text(root.querySelector('[data-waiting-list]')!)).toContain('Con của cô Lan');
    const refused = root.querySelector<HTMLDetailsElement>('details[data-fold="refused"]')!;
    expect(refused.open).toBe(false);   // folded, its count in its title
    expect(text(refused.querySelector('summary')!)).toContain('Đã từ chối (1)');
    expect(root.querySelector('[data-refused-list] [data-join-request="spam"] [data-delete-request]')).not.toBeNull();
    expect(root.querySelector('[data-waiting-list] [data-join-request="bi"] [data-refuse]')).not.toBeNull();
  });

  it('approves, refuses and deletes, then asks for everything again', async () => {
    const a = api();
    mountOwner(root, a, vi.fn(), 'https://gom.test', 'requests');
    await flush(); await flush();
    root.querySelector<HTMLButtonElement>('[data-join-request="bi"] [data-approve]')!.click();
    await flush(); await flush();
    expect(a.approveJoin).toHaveBeenCalledWith('bi');
    expect(a.signUpRequests).toHaveBeenCalledTimes(2);
    root.querySelector<HTMLButtonElement>('[data-join-request="bi"] [data-refuse]')!.click();
    await flush();
    expect(a.refuseJoin).not.toHaveBeenCalled();   // asked first, in the row
    expect(text(root.querySelector('[data-join-request="bi"] [data-refuse-ask]')!)).toContain('Từ chối Bi?');
    root.querySelector<HTMLButtonElement>('[data-join-request="bi"] [data-refuse-no]')!.click();
    await flush();
    expect(root.querySelector('[data-refuse-ask]')).toBeNull();
    root.querySelector<HTMLButtonElement>('[data-join-request="bi"] [data-refuse]')!.click();
    await flush();
    root.querySelector<HTMLButtonElement>('[data-join-request="bi"] [data-refuse-yes]')!.click();
    await flush(); await flush();
    expect(a.refuseJoin).toHaveBeenCalledWith('bi');
    root.querySelector<HTMLButtonElement>('[data-join-request="spam"] [data-delete-request]')!.click();
    await flush(); await flush();
    expect(a.deleteJoin).toHaveBeenCalledWith('spam');
  });

  it('opens and closes asking to join', async () => {
    const a = api({ signUpRequests: vi.fn(async () => ok({ open: false, requests: [] })) });
    mountOwner(root, a, vi.fn(), 'https://gom.test', 'requests');
    await flush(); await flush();
    expect(root.querySelector('[data-none-waiting]')).toBeNull();   // closed and nobody waiting: no empty list
    const box = root.querySelector<HTMLInputElement>('[data-sign-up-open]')!;
    box.checked = true;
    box.dispatchEvent(new Event('change'));
    await flush();
    expect(a.setSignUpOpen).toHaveBeenCalledWith(true);
    expect(root.querySelector('[data-none-waiting]')).not.toBeNull();
  });
});

describe('the API: a waiting account', () => {
  it('calls the page back when the server says the account is still waiting, and only then', async () => {
    const onPending = vi.fn();
    const answers = [
      new Response(JSON.stringify({ error: 'pending_approval' }), { status: 403 }),
      new Response(JSON.stringify({ error: 'not_allowed' }), { status: 403 }),
    ];
    const api = createApi(async () => answers.shift()!, undefined, { onPending });
    const first = await api.conversations();
    expect(first.ok ? '' : first.error).toBe('pending_approval');
    expect(onPending).toHaveBeenCalledOnce();
    await api.conversations();
    expect(onPending).toHaveBeenCalledOnce();
  });
});
