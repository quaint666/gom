import { html, svg, type TemplateResult } from 'lit-html';

const icon = (...paths: string[]): TemplateResult =>
  html`<svg class="i" viewBox="0 0 24 24" aria-hidden="true" focusable="false">${paths.map((d) => svg`<path d=${d}></path>`)}</svg>`;

export const logo = () => icon('M4 5h16v11H11l-4 4v-4H4z', 'M8 10h8');
export const eye = () => icon('M2 12s3.6-6.5 10-6.5S22 12 22 12s-3.6 6.5-10 6.5S2 12 2 12z', 'M12 9a3 3 0 100 6 3 3 0 000-6z');
export const alert = () => icon('M12 3a9 9 0 100 18 9 9 0 000-18z', 'M12 7.5v5.5M12 16.5h.01');
export const clock = () => icon('M12 3a9 9 0 100 18 9 9 0 000-18z', 'M12 7v5l3 2');
export const back = () => icon('M15 5l-7 7 7 7');
export const moon = () => icon('M20 14.5A8 8 0 019.5 4a8 8 0 1010.5 10.5z');
export const sun = () => icon('M12 8a4 4 0 100 8 4 4 0 000-8z', 'M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4');
export const copy = () => icon('M9 9h10v10H9z', 'M5 15V5h10');
export const gear = () => icon(
  'M12 15a3 3 0 100-6 3 3 0 000 6z',
  'M19.4 15a1.7 1.7 0 00.3 1.8l.1.1a2 2 0 11-2.8 2.8l-.1-.1a1.7 1.7 0 00-1.8-.3 1.7 1.7 0 00-1 1.5V21a2 2 0 11-4 0v-.1a1.7 1.7 0 00-1.1-1.5 1.7 1.7 0 00-1.8.3l-.1.1a2 2 0 11-2.8-2.8l.1-.1a1.7 1.7 0 00.3-1.8 1.7 1.7 0 00-1.5-1H3a2 2 0 110-4h.1a1.7 1.7 0 001.5-1.1 1.7 1.7 0 00-.3-1.8l-.1-.1a2 2 0 112.8-2.8l.1.1a1.7 1.7 0 001.8.3H9a1.7 1.7 0 001-1.5V3a2 2 0 114 0v.1a1.7 1.7 0 001 1.5 1.7 1.7 0 001.8-.3l.1-.1a2 2 0 112.8 2.8l-.1.1a1.7 1.7 0 00-.3 1.8V9a1.7 1.7 0 001.5 1H21a2 2 0 110 4h-.1a1.7 1.7 0 00-1.5 1z',
);
export const users = () => icon('M9 11a3.5 3.5 0 100-7 3.5 3.5 0 000 7z', 'M2.5 20c0-3.5 2.9-6 6.5-6s6.5 2.5 6.5 6', 'M16 4.3a3.5 3.5 0 010 6.4', 'M18 14.3c2 .7 3.5 2.5 3.5 5.7');
export const send = () => icon('M4 12l16-8-6 16-3-7-7-1z');
export const wifiOff = () => icon('M2 8.8a15 15 0 0120 0', 'M5 12.5a10 10 0 0114 0', 'M8.5 16a5 5 0 017 0', 'M12 19.5h.01', 'M3 3l18 18');
export const pencil = () => icon('M4 20h4L19 9l-4-4L4 16v4z', 'M13.5 6.5l4 4');
export const more = () => icon('M12 5.5h.01', 'M12 12h.01', 'M12 18.5h.01');
export const checks = () => icon('M2 13l4 4 8-9', 'M10 15l2 2 9-10');
export const close = () => icon('M6 6l12 12', 'M18 6L6 18');
