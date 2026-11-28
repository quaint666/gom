import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mountWelcome } from '../Scripts/pages/welcome.js';
import { fail, fakeApi, flush, ok, text } from './helpers.js';

let root: HTMLElement;
beforeEach(() => { root = document.createElement('div'); document.body.replaceChildren(root); });

const me = { id: 'u1', gomId: 'lan', displayName: 'Lan', isOwner: false };
const testers = { id: 'g1', kind: 'group', title: 'Testers', role: 'member' };
const family = { id: 'g2', kind: 'group', title: 'Nhà', role: 'member' };
const chat = { id: 'c1', kind: 'direct', title: 'Chủ Gom', role: null };

describe('welcome page', () => {
  it('greets by name, says what Gom is, and leads into the group the invite put them in', async () => {
    const go = vi.fn();
    mountWelcome(root, fakeApi({ me: vi.fn(async () => ok(me)), conversations: vi.fn(async () => ok([chat, testers, family])) }), go, 'g1');
    await flush(); await flush();
    expect(text(root.querySelector('h1')!)).toBe('Chào mừng Lan đến Gom');
    expect(root.querySelector('[data-welcome="invite-only"]')).not.toBeNull();
    expect(root.querySelector('[data-welcome="own-server"]')).not.toBeNull();
    expect(text(root.querySelector('[data-welcome="group"]')!)).toContain('Bạn đã vào nhóm Testers');
    root.querySelector<HTMLAnchorElement>('[data-welcome-group]')!.click();
    expect(go).toHaveBeenCalledWith('/c/g1');
  });

  it('without a group from the link, uses their only group (an approval into one)', async () => {
    mountWelcome(root, fakeApi({ me: vi.fn(async () => ok(me)), conversations: vi.fn(async () => ok([chat, family])) }), vi.fn(), null);
    await flush(); await flush();
    expect(text(root.querySelector('[data-welcome-group]')!)).toBe('Vào nhóm Nhà');
  });

  it('with no group, starts at the chat list; the picture button goes to the profile', async () => {
    const go = vi.fn();
    mountWelcome(root, fakeApi({ me: vi.fn(async () => ok(me)), conversations: vi.fn(async () => ok([chat, testers, family])) }), go, null);
    await flush(); await flush();
    expect(root.querySelector('[data-welcome="group"]')).toBeNull();   // two groups and none named: no guess
    root.querySelector<HTMLAnchorElement>('[data-welcome-start]')!.click();
    expect(go).toHaveBeenCalledWith('/');
    root.querySelector<HTMLAnchorElement>('[data-welcome-picture]')!.click();
    expect(go).toHaveBeenCalledWith('/settings/profile');
  });

  it('links to the source code when there is one', async () => {
    mountWelcome(root, fakeApi({ me: vi.fn(async () => ok(me)), conversations: vi.fn(async () => ok([])) }), vi.fn(), null, 'https://github.com/quaint666/gom');
    await flush();
    expect(root.querySelector('[data-source-link]')!.getAttribute('href')).toBe('https://github.com/quaint666/gom');
  });

  it('sends someone signed out to sign in', async () => {
    const go = vi.fn();
    mountWelcome(root, fakeApi({ me: vi.fn(async () => fail('unauthorized', 401)) }), go, null);
    await flush();
    expect(go).toHaveBeenCalledWith('/sign-in');
  });
});
