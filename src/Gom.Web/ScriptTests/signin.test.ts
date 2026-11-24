import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mountSignIn } from '../Scripts/pages/signin.js';
import { fail, fakeApi, fill, flush, ok, submit, text } from './helpers.js';

const user = { gomId: 'me', displayName: 'Me', isOwner: false };
let root: HTMLElement;
beforeEach(() => { root = document.createElement('div'); document.body.replaceChildren(root); });

describe('sign-in page', () => {
  it('has a real form with the right autofill hints and a labelled, required, non-autocorrected ID', () => {
    mountSignIn(root, fakeApi(), vi.fn());
    const id = root.querySelector<HTMLInputElement>('input[name="gomId"]')!;
    const pw = root.querySelector<HTMLInputElement>('input[type="password"]')!;
    expect(id.autocomplete).toBe('username');
    expect(id.required).toBe(true);
    expect(id.getAttribute('autocapitalize')).toBe('none');
    expect(id.getAttribute('spellcheck')).toBe('false');
    expect(root.querySelector(`label[for="${id.id}"]`)).not.toBeNull();
    expect(pw.autocomplete).toBe('current-password');
    expect(pw.required).toBe(true);
    expect(root.querySelector('button[type="submit"]')!.textContent).toContain('Đăng nhập');
  });

  it('signs in and goes to the home page', async () => {
    const api = fakeApi({ signIn: vi.fn(async () => ok(user)) });
    const go = vi.fn();
    mountSignIn(root, api, go);
    fill(root, 'input[name="gomId"]', 'me');
    fill(root, 'input[type="password"]', 'secret password');
    submit(root);
    await flush();
    expect(api.signIn).toHaveBeenCalledWith('me', 'secret password');
    expect(go).toHaveBeenCalledWith('/');
  });

  it('shows the wrong-password sentence in an alert and keeps what was typed', async () => {
    const api = fakeApi({ signIn: vi.fn(async () => fail('invalid_credentials', 401)) });
    const go = vi.fn();
    mountSignIn(root, api, go);
    fill(root, 'input[name="gomId"]', 'me');
    fill(root, 'input[type="password"]', 'nope nope nope');
    submit(root);
    await flush();
    expect(text(root.querySelector('[role="alert"]')!)).toContain('Gom ID hoặc mật khẩu không đúng');
    expect(root.querySelector<HTMLInputElement>('input[name="gomId"]')!.value).toBe('me');
    expect(root.querySelector<HTMLInputElement>('input[type="password"]')!.value).toBe('nope nope nope');
    expect(go).not.toHaveBeenCalled();
  });

  it('disables the button while waiting so a double tap sends one request', async () => {
    let release!: () => void;
    const api = fakeApi({ signIn: vi.fn(() => new Promise((resolve) => { release = () => resolve(ok(user)); })) });
    mountSignIn(root, api, vi.fn());
    fill(root, 'input[name="gomId"]', 'me');
    fill(root, 'input[type="password"]', 'secret password');
    submit(root);
    submit(root);
    await flush();
    expect(api.signIn).toHaveBeenCalledTimes(1);
    expect(root.querySelector<HTMLButtonElement>('button[type="submit"]')!.disabled).toBe(true);
    release();
    await flush();
  });

  it('tells how long to wait after too many tries', async () => {
    const api = fakeApi({ signIn: vi.fn(async () => fail('too_many_attempts', 429, 9)) });
    mountSignIn(root, api, vi.fn());
    fill(root, 'input[name="gomId"]', 'me');
    fill(root, 'input[type="password"]', 'x');
    submit(root);
    await flush();
    expect(text(root.querySelector('[role="alert"]')!)).toContain('9');
  });

  it('can show and hide the password', () => {
    mountSignIn(root, fakeApi(), vi.fn());
    const pw = root.querySelector<HTMLInputElement>('input[name="password"]')!;
    const toggle = root.querySelector<HTMLButtonElement>('button[aria-pressed]')!;
    expect(pw.type).toBe('password');
    toggle.click();
    return flush().then(() => {
      expect(root.querySelector<HTMLInputElement>('input[name="password"]')!.type).toBe('text');
      expect(root.querySelector('button[aria-pressed]')!.getAttribute('aria-pressed')).toBe('true');
    });
  });
});
