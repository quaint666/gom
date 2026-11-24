import { render } from 'lit-html';
import { describe, expect, it } from 'vitest';
import * as icons from '../Scripts/icons.js';

const SVG = 'http://www.w3.org/2000/svg';

describe('icons', () => {
  // A path made by a nested html`` template lands in the HTML namespace and is silently never drawn.
  it.each(Object.entries(icons))('%s draws real SVG paths', (_name, make) => {
    const host = document.createElement('div');
    render(make(), host);
    const paths = host.querySelectorAll('path');
    expect(paths.length).toBeGreaterThan(0);
    for (const p of paths) expect(p.namespaceURI).toBe(SVG);
    expect(host.querySelector('svg')!.getAttribute('aria-hidden')).toBe('true');
  });
});
