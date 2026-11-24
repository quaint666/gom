// Bundles the TypeScript (wwwroot/js/main.js) and then makes a fingerprinted copy of each file a page loads, named after its
// content (main.<hash>.js), with a list of them in wwwroot/assets.json. The server writes those names into the pages, so a file
// can be kept by the browser for a year and a changed file is simply a new name. The plain names stay, for anything that still
// asks for them (the offline page, a page already open).
//
//   node build.mjs           the bundle, minified, and the copies
//   node build.mjs --watch   rebuilds the bundle on every change, with no copies (and removes the list, so pages use plain names)
import { createHash } from 'node:crypto';
import { copyFile, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { build, context } from 'esbuild';

const FILES = ['wwwroot/js/main.js', 'wwwroot/css/gom.css', 'wwwroot/theme-init.js'];
const LIST = 'wwwroot/assets.json';
/** The same length as the server checks for (Assets.cs). */
const HASH_LENGTH = 10;

const bundle = (watching) => ({
  entryPoints: ['Scripts/main.ts'], bundle: true, format: 'esm', target: 'es2022', sourcemap: true, minify: !watching,
  outfile: 'wwwroot/js/main.js',
});

if (process.argv.includes('--watch')) {
  await rm(LIST, { force: true });
  const watcher = await context(bundle(true));
  await watcher.watch();
} else {
  await build(bundle(false));
  const list = {};
  for (const file of FILES) {
    const hash = createHash('sha256').update(await readFile(file)).digest('hex').slice(0, HASH_LENGTH);
    const { dir, name, ext } = path.parse(file);
    const copy = path.join(dir, `${name}.${hash}${ext}`);
    // Copies of an older version are of no use.
    const old = new RegExp(`^${name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\.[0-9a-f]{${HASH_LENGTH}}${ext.replace('.', '\\.')}$`);
    for (const entry of await readdir(dir)) if (old.test(entry) && entry !== path.basename(copy)) await rm(path.join(dir, entry));
    await copyFile(file, copy);
    list['/' + file.replace(/^wwwroot\//, '')] = '/' + copy.replace(/^wwwroot\//, '');
  }
  await writeFile(LIST, JSON.stringify(list, null, 2) + '\n');
}
