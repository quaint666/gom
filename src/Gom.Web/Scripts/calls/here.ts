/**
 * Whether this browser rings for Gom calls: a choice per device, in Settings › Thông báo. Unchosen, a phone's browser does not ring
 * when its person has the Android app taking calls (`appCalls` in /api/me, remembered here by the last page that asked): the app rings
 * that phone, and a Gom tab left open in its browser would ring beside it with its own tone. A computer rings as before.
 */

const CHOICE = 'gom-take-calls';
const APP = 'gom-app-calls';

/** What the person chose for this browser, or null when they have not. */
export function chosenTakeCalls(): boolean | null {
  try {
    const value = localStorage.getItem(CHOICE);
    return value === '1' ? true : value === '0' ? false : null;
  } catch { return null; }
}

export function chooseTakeCalls(on: boolean): void {
  try { localStorage.setItem(CHOICE, on ? '1' : '0'); } catch { /* site data blocked: the default applies */ }
}

/** Remembers whether the person's app takes calls, for the default (a page with no answer yet uses the last one heard). */
export function rememberAppCalls(on: boolean): void {
  try { localStorage.setItem(APP, on ? '1' : '0'); } catch { /* the default stays "ring" */ }
}

/** Whether this browser rings: the choice, or else not on a phone whose person has the app. */
export function takesCallsHere(phone: boolean): boolean {
  const chosen = chosenTakeCalls();
  if (chosen !== null) return chosen;
  try { return !(phone && localStorage.getItem(APP) === '1'); } catch { return true; }
}
