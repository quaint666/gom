import { describe, expect, it } from 'vitest';
import { isAppFile } from '../Scripts/appfiles.js';

describe('app files', () => {
  it('knows files that install or run something by their ending, whatever the case', () => {
    for (const name of ['thiep.apk', 'BANK.APK', 'game.xapk', 'setup.exe', 'cai-dat.msi', 'run.bat', 'a.lnk', 'hoa-don.xlsm', 'x.ps1 ']) expect(isAppFile(name), name).toBe(true);
  });
  it('leaves pictures, documents, archives and names without an ending alone', () => {
    for (const name of ['anh.jpg', 'hop-dong.docx', 'bang.xlsx', 'goi.zip', 'apk', 'notes.txt', 'apk.txt', '']) expect(isAppFile(name), name).toBe(false);
  });
});
