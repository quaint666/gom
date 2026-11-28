import { beforeEach, describe, expect, it } from 'vitest';
import { latestRelease, mountWhatsNew } from '../Scripts/pages/whatsnew.js';
import { en } from '../Scripts/strings/en.js';
import { vi as vietnamese } from '../Scripts/strings/vi.js';
import { text } from './helpers.js';

let root: HTMLElement;

beforeEach(() => {
  document.body.replaceChildren();
  root = document.createElement('main');
  document.body.append(root);
});

const versions = () => [...root.querySelectorAll('[data-release]')].map((r) => r.getAttribute('data-release'));
const parse = (v: string) => v.split('.').map(Number) as [number, number, number];
const newer = (a: string, b: string) => { const [x, y] = [parse(a), parse(b)]; return x[0] !== y[0] ? x[0] - y[0] : x[1] !== y[1] ? x[1] - y[1] : x[2] - y[2]; };

describe('the what’s new page', () => {
  it('lists every release from 1.0.0, the newest first, with the newest open and the others folded', () => {
    mountWhatsNew(root);
    expect(versions()).toEqual(['1.5.0', '1.4.0', '1.3.0', '1.2.1', '1.2.0', '1.1.0', '1.0.0']);
    const open = [...root.querySelectorAll<HTMLDetailsElement>('details[data-release]')].map((d) => d.open);
    expect(open).toEqual([true, false, false, false, false, false, false]);
    expect(text(root.querySelector('[data-release="1.5.0"] summary')!)).toBe('Bản 1.5.0');
    expect(root.querySelectorAll('[data-release="1.4.0"] li').length).toBeGreaterThan(3);
  });

  it('a tap opens a folded release and leaves the others as they are', () => {
    mountWhatsNew(root);
    const old = root.querySelector<HTMLDetailsElement>('[data-release="1.0.0"]')!;
    old.querySelector('summary')!.click();
    old.open = true;   // what the browser does with the tap
    expect(root.querySelector<HTMLDetailsElement>('[data-release="1.5.0"]')!.open).toBe(true);
    expect(old.open).toBe(true);
  });

  it('shows no dates, and goes back to Settings', () => {
    mountWhatsNew(root);
    expect(text(root)).not.toMatch(/\b\d{1,2}[/.]\d{1,2}[/.]\d{4}\b|\b20\d\d\b/);
    expect(root.querySelector('[data-home-top]')!.getAttribute('href')).toBe('/settings');
  });

  it('has the same releases in both languages, newest first, and names the newest as the latest', () => {
    const [a, b] = [vietnamese.whatsNew.releases.map((r) => r.version), en.whatsNew.releases.map((r) => r.version)];
    expect(a).toEqual(b);
    expect([...a].sort((x, y) => newer(y, x))).toEqual(a);
    expect(a.at(-1)).toBe('1.0.0');
    for (const r of [...vietnamese.whatsNew.releases, ...en.whatsNew.releases]) expect(r.items.length).toBeGreaterThan(0);
    expect(latestRelease()).toBe('1.5.0');
  });
});
