import { beforeEach, describe, expect, it } from 'vitest';
import { helpDevice, mountHelp } from '../Scripts/pages/help.js';
import { en } from '../Scripts/strings/en.js';
import { vi } from '../Scripts/strings/vi.js';
import joinSource from '../Scripts/pages/join.ts?raw';
import settingsSource from '../Scripts/pages/settings.ts?raw';
import signinSource from '../Scripts/pages/signin.ts?raw';
import signupSource from '../Scripts/pages/signup.ts?raw';
import waitingSource from '../Scripts/pages/waiting.ts?raw';
import welcomeSource from '../Scripts/pages/welcome.ts?raw';

let root: HTMLElement;

beforeEach(() => {
  document.body.replaceChildren();
  root = document.createElement('main');
  document.body.append(root);
});

const parts = () => [...root.querySelectorAll('[data-help-part]')].map((p) => p.getAttribute('data-help-part'));
const nav = (userAgent: string, platform = 'Linux x86_64', maxTouchPoints = 0) => ({ userAgent, platform, maxTouchPoints });

describe('the setup guide', () => {
  it('puts the part for this device first and marks it, then the others', () => {
    mountHelp(root, 'android');
    expect(parts()).toEqual(['android', 'web', 'iphone']);
    expect(root.querySelectorAll('[data-this-device]')).toHaveLength(1);
    expect(root.querySelector('[data-help-part="android"] [data-this-device]')?.textContent).toContain('Máy này');
    root.replaceWith(root = document.createElement('main'));
    document.body.append(root);
    mountHelp(root, 'iphone');
    expect(parts()).toEqual(['iphone', 'web', 'android']);
  });

  it('tells Android app users to let Gom use the battery without limits, and iPhone users to add Gom to the Home Screen first', () => {
    mountHelp(root, 'web');
    expect(root.querySelector('[data-help-part="android"]')?.textContent).toContain('Pin › Không hạn chế');
    expect(root.querySelector('[data-help-part="iphone"]')?.textContent).toContain('Thêm vào MH chính');
    expect(root.querySelector('[data-help-part="web"]')?.textContent).toContain('Micrô');
  });

  it('knows the app, an iPhone, an iPad that says it is a Mac, and any other browser', () => {
    expect(helpDevice(true, nav('Mozilla/5.0 (Linux; Android 15)'))).toBe('android');
    expect(helpDevice(false, nav('Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X)'))).toBe('iphone');
    expect(helpDevice(false, nav('Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)', 'MacIntel', 5))).toBe('iphone');
    expect(helpDevice(false, nav('Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)', 'MacIntel', 0))).toBe('web');
    expect(helpDevice(false, nav('Mozilla/5.0 (Linux; Android 15) Chrome/140'))).toBe('web');
  });

  it('has as many steps in English as in Vietnamese', () => {
    for (const d of ['android', 'web', 'iphone'] as const) expect(en.help[d].items).toHaveLength(vi.help[d].items.length);
  });

  it('is linked from every page before signing in, the welcome page and Settings', () => {
    for (const source of [signinSource, signupSource, joinSource, waitingSource, welcomeSource, settingsSource]) expect(source).toContain('href="/help" data-help-link');
  });
});
