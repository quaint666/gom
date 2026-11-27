import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mountSettings, sectionOf, type SettingsNav } from '../Scripts/pages/settings.js';
import { fail, fakeApi, fill, flush, ok, text } from './helpers.js';
import { chosenTakeCalls } from '../Scripts/calls/here.js';

let root: HTMLElement;
beforeEach(() => { root = document.createElement('div'); document.body.replaceChildren(root); });

const me = { id: 'u1', gomId: 'mom', displayName: 'Mẹ', isOwner: false, readReceipts: true };
const sessions = [
  { id: 's1', deviceLabel: 'Chrome on Android', current: true, lastSeenAt: '2026-11-26T08:00:00Z' },
  { id: 's2', deviceLabel: 'Firefox on Linux', current: false, lastSeenAt: '2026-11-25T08:00:00Z' },
];
const api = (over: Parameters<typeof fakeApi>[0] = {}) => fakeApi({
  me: vi.fn(async () => ok(me)), sessions: vi.fn(async () => ok(sessions)), blocks: vi.fn(async () => ok([])),
  pushState: vi.fn(async () => fail('unknown', 404)), ...over,
});

/** The address bar, by hand: where it is, what was pushed, and a way to press Back. */
function fakeNav(start: string): SettingsNav & { at: string; pushed: string[]; replaced: string[]; back(to: string): void } {
  let pop: () => void = () => undefined;
  const nav = {
    at: start, pushed: [] as string[], replaced: [] as string[],
    path: () => nav.at,
    push: (p: string) => { nav.at = p; nav.pushed.push(p); },
    replace: (p: string) => { nav.at = p; nav.replaced.push(p); },
    onPop: (callback: () => void) => { pop = callback; },
    back: (to: string) => { nav.at = to; pop(); },
  };
  return nav;
}

const open = () => root.querySelector<HTMLElement>('.settings')!.dataset['open'];
const visible = () => [...root.querySelectorAll<HTMLElement>('.part')].filter((p) => !p.hidden).map((p) => p.dataset['part']);
const click = (el: Element, init: MouseEventInit = {}) => el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, button: 0, ...init }));

describe('settings: the parts and their addresses', () => {
  it('knows a part by its address, and takes anything else for the menu', () => {
    expect(sectionOf('/settings')).toBeNull();
    expect(sectionOf('/settings/appearance')).toBe('appearance');
    expect(sectionOf('/settings/account/')).toBe('account');
    expect(sectionOf('/settings/nope')).toBeNull();
    expect(sectionOf('/settings/account/more')).toBeNull();
  });

  it('lists each part with what it is set to now', async () => {
    mountSettings(root, api(), vi.fn(), undefined, undefined, fakeNav('/settings'));
    await flush();
    const row = (s: string) => text(root.querySelector(`[data-section-link="${s}"]`)!);
    expect(row('appearance')).toContain('Giao diện và ngôn ngữ');
    expect(row('appearance')).toContain('Xanh lá');
    expect(row('privacy')).toContain('Báo đã xem: bật');
    expect(row('account')).toContain('Mật khẩu · 2 thiết bị đang đăng nhập');
    expect(row('notifications')).toContain('Không có trên thiết bị này');
    expect(root.querySelector('[data-section-link="owner"]')).toBeNull();
    expect(open()).toBe('');
  });

  it('opens a part in place with its own address, and Back returns to the menu', async () => {
    const nav = fakeNav('/settings');
    mountSettings(root, api(), vi.fn(), undefined, undefined, nav);
    await flush();
    expect(visible()).toEqual(['profile']);   // beside the menu on a wide screen; a phone shows only the menu
    click(root.querySelector('[data-section-link="appearance"]')!);
    await flush();
    expect(nav.pushed).toEqual(['/settings/appearance']);
    expect(open()).toBe('appearance');
    expect(visible()).toEqual(['appearance']);
    expect(text(root.querySelector('.settings-pane h1')!)).toBe('Giao diện và ngôn ngữ');
    expect(root.querySelector('[data-section-link="appearance"]')!.getAttribute('aria-current')).toBe('page');
    nav.back('/settings');
    await flush();
    expect(open()).toBe('');
  });

  it('leaves a click with a modifier key to the browser (a new tab)', async () => {
    const nav = fakeNav('/settings');
    mountSettings(root, api(), vi.fn(), undefined, undefined, nav);
    await flush();
    const link = root.querySelector('[data-section-link="privacy"]')!;
    const event = new MouseEvent('click', { bubbles: true, cancelable: true, button: 0, ctrlKey: true });
    link.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(false);
    expect(nav.pushed).toEqual([]);
  });

  it('starts at the part its address names', async () => {
    mountSettings(root, api(), vi.fn(), undefined, undefined, fakeNav('/settings/account'));
    await flush();
    expect(open()).toBe('account');
    expect(visible()).toEqual(['account']);
    expect(root.querySelector('[data-part="account"] form[data-password]')).not.toBeNull();
  });

  it('sends a member who reaches an owner part to the menu, and the owner sees both owner parts', async () => {
    const nav = fakeNav('/settings/storage');
    mountSettings(root, api(), vi.fn(), undefined, undefined, nav);
    await flush();
    expect(nav.replaced).toEqual(['/settings']);
    expect(open()).toBe('');
    expect(root.querySelector('[data-part="storage"]')).toBeNull();

    const ownerRoot = document.createElement('div');
    document.body.append(ownerRoot);
    mountSettings(ownerRoot, api({ me: vi.fn(async () => ok({ ...me, isOwner: true })) }), vi.fn(), undefined, undefined, fakeNav('/settings'));
    await flush();
    expect(ownerRoot.querySelector('[data-section-link="owner"]')).not.toBeNull();
    expect(ownerRoot.querySelector('[data-section-link="storage"]')).not.toBeNull();
    expect(ownerRoot.querySelector('[data-delete-zone]')).toBeNull();   // the owner cannot delete the account
  });

  it('sends someone signed out to sign in, and back to the same part afterwards', async () => {
    const go = vi.fn();
    mountSettings(root, api({ me: vi.fn(async () => fail('unauthorized', 401)) }), go, undefined, undefined, fakeNav('/settings/account'));
    await flush();
    expect(go).toHaveBeenCalledWith('/sign-in?next=%2Fsettings%2Faccount');
  });
});

describe('settings: reloading Gom', () => {
  it('shows the version and reloads from the menu', async () => {
    const reload = vi.fn();
    mountSettings(root, api(), vi.fn(), undefined, undefined, fakeNav('/settings'), { version: '1.4.2', reload });
    await flush();
    const row = root.querySelector<HTMLButtonElement>('[data-reload-app]')!;
    expect(text(row)).toContain('Tải lại Gom');
    expect(text(row)).toContain('Phiên bản v1.4.2 · lấy bản mới nhất');
    row.click();
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('says only "get the latest" without a version, and has no row when the page gives no way to reload', async () => {
    mountSettings(root, api(), vi.fn(), undefined, undefined, fakeNav('/settings'), { version: null, reload: vi.fn() });
    await flush();
    expect(text(root.querySelector('[data-reload-app]')!)).toContain('Lấy bản mới nhất');
    const other = document.createElement('div');
    document.body.append(other);
    mountSettings(other, api(), vi.fn(), undefined, undefined, fakeNav('/settings'));
    await flush();
    expect(other.querySelector('[data-reload-app]')).toBeNull();
  });
});

describe('settings: calls in this browser', () => {
  const callsOn = (appCalls: boolean) => api({ me: vi.fn(async () => ok({ ...me, calls: true, appCalls })) });
  const box = () => root.querySelector<HTMLInputElement>('[data-calls-here] input');
  beforeEach(() => { localStorage.clear(); });

  it('offers the switch only in a browser that can take calls, when calls are on', async () => {
    mountSettings(root, callsOn(false), vi.fn(), undefined, undefined, fakeNav('/settings/notifications'));
    await flush();
    expect(box()).toBeNull();   // the app, or a browser without WebRTC
    mountSettings(root, api(), vi.fn(), undefined, undefined, fakeNav('/settings/notifications'), undefined, true);
    await flush();
    expect(box()).toBeNull();   // calls are off on this server
    mountSettings(root, callsOn(false), vi.fn(), undefined, undefined, fakeNav('/settings/notifications'), undefined, true);
    await flush();
    expect(text(root.querySelector('[data-part="notifications"]')!)).toContain('Nhận cuộc gọi Gom trên trình duyệt này');
    expect(box()!.checked).toBe(true);   // a computer rings by default
  });

  it('remembers that the app takes calls, and keeps the choice made for this browser', async () => {
    mountSettings(root, callsOn(true), vi.fn(), undefined, undefined, fakeNav('/settings/notifications'), undefined, true);
    await flush();
    expect(localStorage.getItem('gom-app-calls')).toBe('1');
    box()!.checked = false;
    box()!.dispatchEvent(new Event('change', { bubbles: true }));
    await flush();
    expect(chosenTakeCalls()).toBe(false);
    expect(box()!.checked).toBe(false);
  });
});

describe('settings: colours', () => {
  it('shows the sixteen solid colours, then the eight gradients under "Chuyển màu", and saves the one chosen', async () => {
    const setPalette = vi.fn(async () => ok(undefined));
    mountSettings(root, api({ setPalette }), vi.fn(), undefined, undefined, fakeNav('/settings/appearance'));
    await flush();
    const codes = (selector: string) => [...root.querySelectorAll<HTMLInputElement>(`${selector} input[name="palette"]`)].map((i) => i.value);
    expect(codes('[data-palette-choice]')).toHaveLength(16);
    expect(codes('[data-gradient-choice]')).toEqual(['sunset', 'nightsea', 'forest', 'blossom', 'ember', 'candy', 'aurora', 'lime']);
    expect(text(root.querySelector('[data-gradient-choice]')!)).toContain('Hoàng hôn');
    const sunset = root.querySelector<HTMLInputElement>('[data-gradient-choice] input[value="sunset"]')!;
    sunset.checked = true;
    sunset.dispatchEvent(new Event('change', { bubbles: true }));
    await flush();
    expect(setPalette).toHaveBeenCalledWith('sunset');
    expect(document.documentElement.dataset['palette']).toBe('sunset');
    delete document.documentElement.dataset['palette'];
  });
});

describe('settings: inside the parts', () => {
  it('lists blocked people under Quyền riêng tư, and unblocks one', async () => {
    const unblock = vi.fn(async () => ok(undefined));
    mountSettings(root, api({ blocks: vi.fn(async () => ok([{ gomId: 'ba', displayName: 'Chú Ba' }])), unblock }), vi.fn(), undefined, undefined, fakeNav('/settings/privacy'));
    await flush();
    const row = root.querySelector('[data-part="privacy"] [data-blocked="ba"]')!;
    expect(text(row)).toContain('Chú Ba');
    row.querySelector('button')!.click();
    await flush();
    expect(unblock).toHaveBeenCalledWith('ba');
    expect(root.querySelector('[data-blocked="ba"]')).toBeNull();
    expect(root.querySelector('[data-blocked-none]')).not.toBeNull();
  });

  it('shows the Gom ID and copies the friend link from Hồ sơ', async () => {
    const writeText = vi.fn(async () => undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    mountSettings(root, api(), vi.fn(), undefined, undefined, fakeNav('/settings/profile'));
    await flush();
    const part = root.querySelector('[data-part="profile"]')!;
    expect(text(part)).toContain('@mom');
    expect(text(part.querySelector('[data-friend-link]')!)).toBe(`${location.origin}/add/mom`);
    part.querySelector<HTMLButtonElement>('[data-copy-link]')!.click();
    await flush();
    expect(writeText).toHaveBeenCalledWith(`${location.origin}/add/mom`);
    expect(text(part)).toContain('Đã sao chép');
  });

  it('keeps deleting the account closed until it is asked for', async () => {
    mountSettings(root, api(), vi.fn(), undefined, undefined, fakeNav('/settings/account'));
    await flush();
    const zone = root.querySelector<HTMLDetailsElement>('[data-delete-zone]')!;
    expect(zone.open).toBe(false);
    expect(text(zone.querySelector('summary')!)).toBe('Xóa tài khoản…');
  });

  it('goes back to the same part after changing the language', async () => {
    const go = vi.fn();
    mountSettings(root, api({ setLanguage: vi.fn(async () => ok(undefined)) }), go, undefined, undefined, fakeNav('/settings/appearance'));
    await flush();
    const english = root.querySelector<HTMLInputElement>('#language-en')!;
    english.checked = true;
    english.dispatchEvent(new Event('change'));
    await flush();
    expect(go).toHaveBeenCalledWith('/settings/appearance');
  });
});

describe('settings: two-step sign-in', () => {
  const key = { key: 'GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ', uri: 'otpauth://totp/Gom:mom?secret=GEZD', qr: 'data:image/png;base64,AAAA' };
  const card = () => root.querySelector<HTMLElement>('[data-part="account"] [data-two-step]')!;
  const fillCard = (code: string, password: string) => {
    fill(card(), 'input[name="code"]', code);
    fill(card(), 'input[name="two-step-password"]', password);
    card().dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
  };

  it('explains it while off, then shows the QR code, the key in fours and a link for the app, and turns on with a code and the password', async () => {
    const twoStepOn = vi.fn(async () => ok(undefined));
    mountSettings(root, api({ me: vi.fn(async () => ok({ ...me })), twoStepNew: vi.fn(async () => ok(key)), twoStepOn }), vi.fn(), undefined, undefined, fakeNav('/settings/account'));
    await flush();
    expect(card().dataset['twoStep']).toBe('off');
    expect(text(card())).toContain('Google Authenticator');
    card().querySelector<HTMLButtonElement>('[data-two-step-start]')!.click();
    await flush();
    expect(card().dataset['twoStep']).toBe('setup');
    expect(card().querySelector<HTMLImageElement>('[data-two-step-qr]')!.getAttribute('src')).toBe(key.qr);
    expect(text(card().querySelector('[data-two-step-key]')!)).toBe('GEZD GNBV GY3T QOJQ GEZD GNBV GY3T QOJQ');
    expect(card().querySelector<HTMLAnchorElement>('[data-two-step-app]')!.getAttribute('href')).toBe(key.uri);
    fillCard('123 456', 'my password');
    await flush();
    expect(twoStepOn).toHaveBeenCalledWith(key.key, '123 456', 'my password');
    expect(card().dataset['twoStep']).toBe('on');
    expect(text(card().querySelector('[data-two-step-status]')!)).toBe('Đã bật xác minh hai bước.');
  });

  it('keeps the key on screen with the reason when the code is wrong', async () => {
    mountSettings(root, api({ me: vi.fn(async () => ok({ ...me })), twoStepNew: vi.fn(async () => ok(key)), twoStepOn: vi.fn(async () => fail('invalid_code', 400)) }), vi.fn(), undefined, undefined, fakeNav('/settings/account'));
    await flush();
    card().querySelector<HTMLButtonElement>('[data-two-step-start]')!.click();
    await flush();
    fillCard('000000', 'my password');
    await flush();
    expect(card().dataset['twoStep']).toBe('setup');
    expect(text(card().querySelector('[role="alert"]')!)).toContain('Mã không đúng');
  });

  it('turns off with a code and the password when on', async () => {
    const twoStepOff = vi.fn(async () => ok(undefined));
    mountSettings(root, api({ me: vi.fn(async () => ok({ ...me, twoStep: true })), twoStepOff }), vi.fn(), undefined, undefined, fakeNav('/settings/account'));
    await flush();
    expect(card().dataset['twoStep']).toBe('on');
    fillCard('654321', 'my password');
    await flush();
    expect(twoStepOff).toHaveBeenCalledWith('654321', 'my password');
    expect(card().dataset['twoStep']).toBe('off');
    expect(text(card().querySelector('[data-two-step-status]')!)).toBe('Đã tắt xác minh hai bước.');
  });
});
