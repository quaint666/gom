import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createApi } from '../Scripts/api.js';
import { mountOwner } from '../Scripts/pages/owner.js';
import { mountSettings } from '../Scripts/pages/settings.js';
import { errorText } from '../Scripts/text.js';
import { fail, fakeApi, fill, flush, ok, text } from './helpers.js';

let root: HTMLElement;
beforeEach(() => { root = document.createElement('div'); document.body.replaceChildren(root); });

const me = { gomId: 'mom', displayName: 'Mẹ', isOwner: false };
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

describe('api: own account and members', () => {
  it('changes the nickname with PATCH /api/me', async () => {
    const fetchFn = vi.fn(async () => json({ ...me, displayName: 'Chị Hai' }));
    const result = await createApi(fetchFn).setNickname('Chị Hai');
    const [url, init] = fetchFn.mock.calls[0] as unknown as [string, RequestInit];
    expect([url, init.method, JSON.parse(init.body as string)]).toEqual(['/api/me', 'PATCH', { displayName: 'Chị Hai' }]);
    expect(result.ok).toBe(true);
  });

  it('changes the password with PUT /api/me/password and reads a 204', async () => {
    const fetchFn = vi.fn(async () => new Response(null, { status: 204 }));
    const result = await createApi(fetchFn).changePassword('old one', 'new one');
    const [url, init] = fetchFn.mock.calls[0] as unknown as [string, RequestInit];
    expect([url, init.method, JSON.parse(init.body as string)]).toEqual(['/api/me/password', 'PUT', { current: 'old one', password: 'new one' }]);
    expect(result).toEqual({ ok: true, data: undefined });
  });

  it('knows the new error codes', async () => {
    expect(await createApi(async () => json({ error: 'wrong_password' }, 400)).changePassword('a', 'b')).toMatchObject({ error: 'wrong_password' });
    expect(await createApi(async () => json({ error: 'cannot_disable_owner' }, 400)).disableMember('boss')).toMatchObject({ error: 'cannot_disable_owner' });
  });

  it('disables and enables a member with POST', async () => {
    const fetchFn = vi.fn(async () => new Response(null, { status: 204 }));
    const api = createApi(fetchFn);
    await api.disableMember('minh');
    await api.enableMember('minh');
    expect(fetchFn.mock.calls.map((c) => [(c as unknown as [string, RequestInit])[0], (c as unknown as [string, RequestInit])[1].method]))
      .toEqual([['/api/users/minh/disable', 'POST'], ['/api/users/minh/enable', 'POST']]);
  });

  it('says in words what a wrong password and a protected owner mean', () => {
    expect(errorText(fail('wrong_password'))).toContain('Mật khẩu hiện tại');
    expect(errorText(fail('cannot_disable_owner'))).toContain('chủ');
  });
});

describe('settings: nickname and password', () => {
  const sessions = [{ id: 's1', deviceLabel: 'Chrome on Android', createdAt: '2026-12-01T09:00:00Z', lastSeenAt: '2026-12-02T09:00:00Z', current: true }];
  const api = (extra = {}) => fakeApi({ me: vi.fn(async () => ok(me)), sessions: vi.fn(async () => ok(sessions)), ...extra });

  it('shows the nickname, saves a new one and says so', async () => {
    const a = api({ setNickname: vi.fn(async (name: string) => ok({ ...me, displayName: name })) });
    mountSettings(root, a, vi.fn());
    await flush();
    expect(root.querySelector<HTMLInputElement>('input[name="displayName"]')!.value).toBe('Mẹ');

    fill(root, 'input[name="displayName"]', 'Chị Hai');
    root.querySelector<HTMLFormElement>('form[data-nickname]')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await flush();

    expect(a.setNickname).toHaveBeenCalledWith('Chị Hai');
    expect(text(root.querySelector('[data-nickname-status]')!)).toContain('Đã lưu');
    expect(text(root)).toContain('Chị Hai');
  });

  it('shows a refused nickname in words and keeps what was typed', async () => {
    const a = api({ setNickname: vi.fn(async () => fail('invalid_display_name')) });
    mountSettings(root, a, vi.fn());
    await flush();
    fill(root, 'input[name="displayName"]', 'x');
    root.querySelector<HTMLFormElement>('form[data-nickname]')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await flush();
    expect(text(root.querySelector('[role="alert"]')!)).toContain('Tên hiển thị');
    expect(root.querySelector<HTMLInputElement>('input[name="displayName"]')!.value).toBe('x');
  });

  it('changes the password, clears the fields, and reloads the device list', async () => {
    const a = api();
    mountSettings(root, a, vi.fn());
    await flush();
    fill(root, 'input[name="current"]', 'old password!');
    fill(root, 'input[name="password"]', 'brand new password');
    root.querySelector<HTMLFormElement>('form[data-password]')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await flush();

    expect(a.changePassword).toHaveBeenCalledWith('old password!', 'brand new password');
    expect(text(root.querySelector('[data-password-status]')!)).toContain('Đã đổi mật khẩu');
    expect(root.querySelector<HTMLInputElement>('input[name="current"]')!.value).toBe('');
    expect(root.querySelector<HTMLInputElement>('input[name="password"]')!.value).toBe('');
    expect(a.sessions).toHaveBeenCalledTimes(2);
  });

  it('says the current password is wrong, and does not reload devices', async () => {
    const a = api({ changePassword: vi.fn(async () => fail('wrong_password')) });
    mountSettings(root, a, vi.fn());
    await flush();
    fill(root, 'input[name="current"]', 'nope nope nope');
    fill(root, 'input[name="password"]', 'brand new password');
    root.querySelector<HTMLFormElement>('form[data-password]')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await flush();
    expect(text(root.querySelector('[role="alert"]')!)).toContain('Mật khẩu hiện tại không đúng');
    expect(a.sessions).toHaveBeenCalledTimes(1);
  });

  it('gives the password manager a username and the right autocomplete hints', async () => {
    mountSettings(root, api(), vi.fn());
    await flush();
    const form = root.querySelector('form[data-password]')!;
    expect(form.querySelector<HTMLInputElement>('input[autocomplete="username"]')!.value).toBe('mom');
    expect(form.querySelector('input[name="current"]')!.getAttribute('autocomplete')).toBe('current-password');
    expect(form.querySelector('input[name="password"]')!.getAttribute('autocomplete')).toBe('new-password');
    const ids = [...form.querySelectorAll('input[id]')].map((i) => i.id);
    expect(new Set(ids).size).toBe(ids.length);
    for (const input of form.querySelectorAll<HTMLInputElement>('input[id]')) {
      expect(form.querySelector(`label[for="${input.id}"]`), `label for #${input.id}`).not.toBeNull();
    }
  });
});

describe('owner: switching members off and on', () => {
  const owner = { gomId: 'boss', displayName: 'Boss', isOwner: true };
  const members = [
    { gomId: 'boss', displayName: 'Boss', isOwner: true, disabled: false, createdAt: '2026-11-01T00:00:00Z' },
    { gomId: 'minh', displayName: 'Minh', isOwner: false, disabled: false, createdAt: '2026-11-25T00:00:00Z' },
    { gomId: 'lan', displayName: 'Lan', isOwner: false, disabled: true, createdAt: '2026-11-26T00:00:00Z' },
  ];
  const api = (extra = {}) => fakeApi({
    me: vi.fn(async () => ok(owner)), invites: vi.fn(async () => ok([])), members: vi.fn(async () => ok(members)), ...extra,
  });

  it('offers to lock an active member, to unlock a locked one, and nothing for the owner', async () => {
    mountOwner(root, api(), vi.fn(), 'https://gom.test');
    await flush();
    expect(root.querySelector('[data-member="boss"] button')).toBeNull();
    expect(root.querySelector('[data-member="minh"] button[data-disable]')).not.toBeNull();
    expect(root.querySelector('[data-member="minh"] button[data-enable]')).toBeNull();
    expect(root.querySelector('[data-member="lan"] button[data-enable]')).not.toBeNull();
    expect(root.querySelector('[data-member="lan"] button[data-disable]')).toBeNull();
    expect(text(root.querySelector('[data-member="lan"]')!)).toContain('Đã khóa');
  });

  it('disables a member and reloads the list', async () => {
    const a = api();
    mountOwner(root, a, vi.fn(), 'https://gom.test');
    await flush();
    root.querySelector<HTMLButtonElement>('[data-member="minh"] button[data-disable]')!.click();
    await flush();
    expect(a.disableMember).toHaveBeenCalledWith('minh');
    expect(a.members).toHaveBeenCalledTimes(2);
  });

  it('enables a member and reloads the list', async () => {
    const a = api();
    mountOwner(root, a, vi.fn(), 'https://gom.test');
    await flush();
    root.querySelector<HTMLButtonElement>('[data-member="lan"] button[data-enable]')!.click();
    await flush();
    expect(a.enableMember).toHaveBeenCalledWith('lan');
    expect(a.members).toHaveBeenCalledTimes(2);
  });

  it('shows a failure in words', async () => {
    const a = api({ disableMember: vi.fn(async () => fail('cannot_disable_owner')) });
    mountOwner(root, a, vi.fn(), 'https://gom.test');
    await flush();
    root.querySelector<HTMLButtonElement>('[data-member="minh"] button[data-disable]')!.click();
    await flush();
    expect(root.querySelector('[role="alert"]')).not.toBeNull();
  });
});

describe('settings: profile and appearance', () => {
  const api = () => fakeApi({ me: vi.fn(async () => ok(me)), sessions: vi.fn(async () => ok([])) });
  const meta = () => document.querySelector('meta[name="color-scheme"]')!.getAttribute('content');
  const radio = (value: string) => root.querySelector<HTMLInputElement>(`input[name="scheme"][value="${value}"]`)!;
  beforeEach(() => { document.head.innerHTML = '<meta name="color-scheme" content="light dark">'; localStorage.clear(); });

  it('shows who you are in a profile card, with an avatar of your initials', async () => {
    mountSettings(root, api(), vi.fn());
    await flush();
    const card = root.querySelector('[data-profile]')!;
    expect(text(card)).toContain('Mẹ');
    expect(text(card)).toContain('@mom');
    expect(card.querySelector('.av')!.textContent).toBe('M');
  });

  it('offers system, light and dark, with the one in use chosen', async () => {
    mountSettings(root, api(), vi.fn());
    await flush();
    expect(['system', 'light', 'dark'].map((v) => radio(v).checked)).toEqual([true, false, false]);
    for (const v of ['system', 'light', 'dark']) expect(root.querySelector(`label[for="${radio(v).id}"]`)).not.toBeNull();
  });

  it('applies and remembers a choice at once, and going back to the system forgets it', async () => {
    mountSettings(root, api(), vi.fn());
    await flush();
    radio('dark').checked = true; radio('dark').dispatchEvent(new Event('change', { bubbles: true })); await flush();
    expect(meta()).toBe('dark');
    expect(localStorage.getItem('color-scheme')).toBe('dark');
    expect(radio('dark').checked).toBe(true);
    radio('light').checked = true; radio('light').dispatchEvent(new Event('change', { bubbles: true })); await flush();
    expect(meta()).toBe('light');
    radio('system').checked = true; radio('system').dispatchEvent(new Event('change', { bubbles: true })); await flush();
    expect(meta()).toBe('light dark');
    expect(localStorage.getItem('color-scheme')).toBeNull();
  });

  it('opens with the stored choice selected', async () => {
    localStorage.setItem('color-scheme', 'dark');
    mountSettings(root, api(), vi.fn());
    await flush();
    expect(radio('dark').checked).toBe(true);
    expect(radio('system').checked).toBe(false);
  });
});
