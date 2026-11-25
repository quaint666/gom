/**
 * Files that install or run something when opened: an Android app, a Windows program or script, an Office file with macros. Scams
 * send these as a greeting card, a bank or a government app, often from a friend's stolen account, so whoever receives one is warned
 * and asked before it is saved. Matched by the name's ending only: a renamed one is saved under its new name, which no phone installs.
 * Archives (.zip, .rar) are left out on purpose: a warning on every one would teach people to ignore it.
 */
export const APP_FILE_ENDINGS: readonly string[] = [
  'apk', 'xapk', 'apks', 'exe', 'msi', 'bat', 'cmd', 'scr', 'lnk', 'ps1', 'js', 'vbs', 'hta', 'docm', 'xlsm', 'pptm',
];

export function isAppFile(name: string): boolean {
  const dot = name.lastIndexOf('.');
  return dot >= 0 && APP_FILE_ENDINGS.includes(name.slice(dot + 1).trim().toLowerCase());
}
