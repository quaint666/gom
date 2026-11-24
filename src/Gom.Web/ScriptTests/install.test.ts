import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createInstaller } from '../Scripts/install.js';
import { mountSettings } from '../Scripts/pages/settings.js';
import { fakeApi, flush, ok, text } from './helpers.js';

/** What Chrome sends: an event with prompt() and the answer in userChoice. */
function offer(outcome: 'accepted' | 'dismissed' = 'accepted') {
  const event = new Event('beforeinstallprompt', { cancelable: true });
  const prompt = vi.fn(async () => undefined);
  Object.assign(event, { prompt, userChoice: Promise.resolve({ outcome }) });
  return { event, prompt };
}

describe('the installer', () => {
  it('has nothing to offer until the browser makes an offer', () => {
    expect(createInstaller(new EventTarget()).available()).toBe(false);
  });

  it('keeps the offer, and stops the browser showing its own', () => {
    const target = new EventTarget();
    const installer = createInstaller(target);
    const { event } = offer();
    target.dispatchEvent(event);
    expect(installer.available()).toBe(true);
    expect(event.defaultPrevented).toBe(true);
  });

  it('asks the browser to install, once, and reports the answer', async () => {
    const target = new EventTarget();
    const installer = createInstaller(target);
    const { event, prompt } = offer('accepted');
    target.dispatchEvent(event);
    expect(await installer.install()).toBe('accepted');
    expect(prompt).toHaveBeenCalledTimes(1);
    expect(installer.available()).toBe(false);
    expect(await installer.install()).toBe('unavailable');
    expect(prompt).toHaveBeenCalledTimes(1);
  });

  it('reports a refusal as dismissed', async () => {
    const target = new EventTarget();
    const installer = createInstaller(target);
    target.dispatchEvent(offer('dismissed').event);
    expect(await installer.install()).toBe('dismissed');
  });

  it('forgets the offer once the app is installed (by this button or the browser menu)', () => {
    const target = new EventTarget();
    const installer = createInstaller(target);
    target.dispatchEvent(offer().event);
    target.dispatchEvent(new Event('appinstalled'));
    expect(installer.available()).toBe(false);
  });

  it('says so when something changes', () => {
    const target = new EventTarget();
    const installer = createInstaller(target);
    const changed = vi.fn();
    installer.onChange(changed);
    target.dispatchEvent(offer().event);
    expect(changed).toHaveBeenCalledTimes(1);
  });
});

describe('settings: install button', () => {
  let root: HTMLElement;
  beforeEach(() => { root = document.createElement('div'); document.body.replaceChildren(root); });
  const api = () => fakeApi({ me: vi.fn(async () => ok({ gomId: 'mom', displayName: 'Mẹ', isOwner: false })), sessions: vi.fn(async () => ok([])) });

  it('shows no install card when the browser has made no offer', async () => {
    mountSettings(root, api(), vi.fn(), createInstaller(new EventTarget()));
    await flush();
    expect(root.querySelector('[data-install]')).toBeNull();
  });

  it('shows the card when an offer comes, even after the page was drawn', async () => {
    const target = new EventTarget();
    mountSettings(root, api(), vi.fn(), createInstaller(target));
    await flush();
    target.dispatchEvent(offer().event);
    await flush();
    expect(root.querySelector('[data-install]')).not.toBeNull();
  });

  it('installs on tap, then replaces the card with a short confirmation', async () => {
    const target = new EventTarget();
    mountSettings(root, api(), vi.fn(), createInstaller(target));
    const { event, prompt } = offer('accepted');
    target.dispatchEvent(event);
    await flush();
    (root.querySelector('[data-install-button]') as HTMLButtonElement).click();
    await flush();
    expect(prompt).toHaveBeenCalledTimes(1);
    expect(root.querySelector('[data-install]')).toBeNull();
    expect(text(root.querySelector('[data-install-status]')!)).toContain('Đã cài đặt');
  });

  it('says nothing more when the person declines', async () => {
    const target = new EventTarget();
    mountSettings(root, api(), vi.fn(), createInstaller(target));
    target.dispatchEvent(offer('dismissed').event);
    await flush();
    (root.querySelector('[data-install-button]') as HTMLButtonElement).click();
    await flush();
    expect(root.querySelector('[data-install-status]')).toBeNull();
  });
});
