import { html, svg, type TemplateResult } from 'lit-html';

const icon = (...paths: string[]): TemplateResult =>
  html`<svg class="i" viewBox="0 0 24 24" aria-hidden="true" focusable="false">${paths.map((d) => svg`<path d=${d}></path>`)}</svg>`;

export const logo = () => icon('M4 5h16v11H11l-4 4v-4H4z', 'M8 10h8');
export const eyeOff = () => icon('M2 12s3.6-6.5 10-6.5c1.6 0 3 .4 4.2 1M22 12s-3.6 6.5-10 6.5c-1.6 0-3-.4-4.2-1', 'M9.9 9.9a3 3 0 004.2 4.2', 'M3 3l18 18');
/** A filled-looking dot in a ring: "unread". */
export const unread = () => icon('M12 3a9 9 0 100 18 9 9 0 000-18z', 'M12 9.5a2.5 2.5 0 100 5 2.5 2.5 0 000-5z');
export const eye = () => icon('M2 12s3.6-6.5 10-6.5S22 12 22 12s-3.6 6.5-10 6.5S2 12 2 12z', 'M12 9a3 3 0 100 6 3 3 0 000-6z');
export const alert = () => icon('M12 3a9 9 0 100 18 9 9 0 000-18z', 'M12 7.5v5.5M12 16.5h.01');
export const clock = () => icon('M12 3a9 9 0 100 18 9 9 0 000-18z', 'M12 7v5l3 2');
export const back = () => icon('M15 5l-7 7 7 7');
export const moon = () => icon('M20 14.5A8 8 0 019.5 4a8 8 0 1010.5 10.5z');
export const sun = () => icon('M12 8a4 4 0 100 8 4 4 0 000-8z', 'M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4');
export const copy = () => icon('M9 9h10v10H9z', 'M5 15V5h10');
/** A row that opens a page of its own. */
export const chevron = () => icon('M9 5l7 7-7 7');
export const lock = () => icon('M6 11h12v9H6z', 'M8.5 11V8a3.5 3.5 0 017 0v3');
export const shield = () => icon('M12 3l7 3v5c0 4.5-3 8.2-7 10-4-1.8-7-5.5-7-10V6z', 'M9 12l2 2 4-4');
export const disk = () => icon('M12 3a9 9 0 100 18 9 9 0 000-18z', 'M12 3v9l6.4 6.4');
export const phone = () => icon('M8 3h8v18H8z', 'M11 18h2');
/** A handset: a voice call. */
export const call = () => icon('M5 4h4l2 5-2.5 1.5a11 11 0 005 5L15 13l5 2v4a2 2 0 01-2 2A16 16 0 013 6a2 2 0 012-2z');
/** The microphone, and the microphone off (muted). */
export const mic = () => icon('M12 3a3 3 0 00-3 3v6a3 3 0 006 0V6a3 3 0 00-3-3z', 'M5 11a7 7 0 0014 0', 'M12 18v3');
export const micOff = () => icon('M12 3a3 3 0 00-3 3v6a3 3 0 006 0V6a3 3 0 00-3-3z', 'M5 11a7 7 0 0014 0', 'M12 18v3', 'M3 3l18 18');
/** A call that came in, and one that went out. */
export const callIn = () => icon('M17 7L7 17', 'M7 9v8h8');
export const callOut = () => icon('M7 17L17 7', 'M9 7h8v8');
export const reload = () => icon('M20 12a8 8 0 1 1-2.3-5.6', 'M20 4v5h-5');
export const camera = () => icon('M4 8h3l2-3h6l2 3h3v11H4z', 'M12 10a3 3 0 100 6 3 3 0 000-6z');
export const gear = () => icon(
  'M12 15a3 3 0 100-6 3 3 0 000 6z',
  'M19.4 15a1.7 1.7 0 00.3 1.8l.1.1a2 2 0 11-2.8 2.8l-.1-.1a1.7 1.7 0 00-1.8-.3 1.7 1.7 0 00-1 1.5V21a2 2 0 11-4 0v-.1a1.7 1.7 0 00-1.1-1.5 1.7 1.7 0 00-1.8.3l-.1.1a2 2 0 11-2.8-2.8l.1-.1a1.7 1.7 0 00.3-1.8 1.7 1.7 0 00-1.5-1H3a2 2 0 110-4h.1a1.7 1.7 0 001.5-1.1 1.7 1.7 0 00-.3-1.8l-.1-.1a2 2 0 112.8-2.8l.1.1a1.7 1.7 0 001.8.3H9a1.7 1.7 0 001-1.5V3a2 2 0 114 0v.1a1.7 1.7 0 001 1.5 1.7 1.7 0 001.8-.3l.1-.1a2 2 0 112.8 2.8l-.1.1a1.7 1.7 0 00-.3 1.8V9a1.7 1.7 0 001.5 1H21a2 2 0 110 4h-.1a1.7 1.7 0 00-1.5 1z',
);
export const users = () => icon('M9 11a3.5 3.5 0 100-7 3.5 3.5 0 000 7z', 'M2.5 20c0-3.5 2.9-6 6.5-6s6.5 2.5 6.5 6', 'M16 4.3a3.5 3.5 0 010 6.4', 'M18 14.3c2 .7 3.5 2.5 3.5 5.7');
export const signOut = () => icon('M9 21H5a2 2 0 01-2-2V5a2 2 0 012-2h4', 'M16 17l5-5-5-5', 'M21 12H9');
export const send = () => icon('M4 12l16-8-6 16-3-7-7-1z');
export const wifiOff = () => icon('M2 8.8a15 15 0 0120 0', 'M5 12.5a10 10 0 0114 0', 'M8.5 16a5 5 0 017 0', 'M12 19.5h.01', 'M3 3l18 18');
export const pencil = () => icon('M4 20h4L19 9l-4-4L4 16v4z', 'M13.5 6.5l4 4');
export const smile = () => icon('M12 3a9 9 0 100 18 9 9 0 000-18z', 'M8 14.5s1.5 2 4 2 4-2 4-2', 'M9 9.5h.01', 'M15 9.5h.01');
export const more = () => icon('M12 5.5h.01', 'M12 12h.01', 'M12 18.5h.01');
export const check = () => icon('M5 12.5l4.5 4.5L19 7.5');
export const block = () => icon('M12 3a9 9 0 100 18 9 9 0 000-18z', 'M5.6 5.6l12.8 12.8');
export const info = () => icon('M12 3a9 9 0 100 18 9 9 0 000-18z', 'M12 11v5.5', 'M12 7.5h.01');
export const attach = () => icon('M20 11.5l-8.2 8.2a5 5 0 01-7.1-7.1l8.5-8.5a3.3 3.3 0 014.7 4.7l-8.5 8.5a1.7 1.7 0 01-2.4-2.4l7.8-7.8');
export const file = () => icon('M6 3h8l4 4v14H6z', 'M14 3v4h4', 'M9 13h6M9 17h6');
export const palette = () => icon('M12 3a9 9 0 100 18c1.1 0 1.6-.7 1.6-1.5 0-.9-.8-1.3-.8-2.1 0-.9.7-1.5 1.6-1.5H16a5 5 0 005-5c0-4.4-4-7.9-9-7.9z', 'M7.5 12h.01', 'M9.5 7.5h.01', 'M14.5 7.5h.01');
export const play = () => icon('M8 5.5v13l10.5-6.5z');
/** A picture: the gallery button (pictures and videos). */
export const image = () => icon('M4 5h16v14H4z', 'M4 16l5-5 4 4 2.5-2.5L20 17', 'M15.5 8.5h.01');
/** A strip of film: a video that goes as a download. */
export const film = () => icon('M4 4h16v16H4z', 'M8 4v16M16 4v16', 'M4 8h4M4 12h4M4 16h4M16 8h4M16 12h4M16 16h4');
export const pause = () => icon('M9 5.5v13', 'M15 5.5v13');
export const download = () => icon('M12 4v11', 'M7.5 11l4.5 4.5 4.5-4.5', 'M5 20h14');
export const next = () => icon('M9 5l7 7-7 7');
export const checks = () => icon('M2 13l4 4 8-9', 'M10 15l2 2 9-10');
export const close = () => icon('M6 6l12 12', 'M18 6L6 18');
export const bell = () => icon('M6 16V11a6 6 0 0112 0v5l1.5 2h-15z', 'M10 21h4');
export const bellOff = () => icon('M6 16V11a6 6 0 0112 0v5l1.5 2h-15z', 'M10 21h4', 'M4 4l16 16');
export const down = () => icon('M12 5v14', 'M6 13l6 6 6-6');
export const search = () => icon('M11 4a7 7 0 100 14 7 7 0 000-14z', 'M20 20l-4-4');
export const pin = () => icon('M9 3h6l-1 6 3 3v2H7v-2l3-3z', 'M12 14v7');
export const reply = () => icon('M9 7L4 12l5 5', 'M4 12h10a6 6 0 016 6v1');
export const forward = () => icon('M15 7l5 5-5 5', 'M20 12H10a6 6 0 00-6 6v1');
export const trash = () => icon('M4 7h16', 'M9 7V4h6v3', 'M6 7l1 13h10l1-13', 'M10 11v6M14 11v6');
export const undo = () => icon('M9 14L4 9l5-5', 'M4 9h10a6 6 0 010 12h-3');
