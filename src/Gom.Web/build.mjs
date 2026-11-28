// Joins the stylesheet's parts (Styles/*.css, in name order) into wwwroot/css/gom.css, bundles the TypeScript (wwwroot/js/main.js)
// and then makes a fingerprinted copy of each file a page loads, named after its
// content (main.<hash>.js), with a list of them in wwwroot/assets.json. The server writes those names into the pages, so a file
// can be kept by the browser for a year and a changed file is simply a new name. The plain names stay, for anything that still
// asks for them (the offline page, a page already open).
//
//   node build.mjs           the bundle, minified, and the copies
//   node build.mjs --watch   rebuilds the bundle and the stylesheet on every change, with no copies (and removes the list, so pages use plain names)
import { createHash } from 'node:crypto';
import { watch } from 'node:fs';
import { copyFile, mkdir, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { build, context } from 'esbuild';

// video.js (the video converter) is its own file: a page loads it only when a video is picked.
const SHEET = 'wwwroot/css/gom.css';
const FILES = ['wwwroot/js/main.js', 'wwwroot/js/video.js', SHEET, 'wwwroot/theme-init.js'];
/** The stylesheet's parts: each starts with a number, so the name order is the cascade order. */
const STYLES = 'Styles';
const LIST = 'wwwroot/assets.json';
/** The same length as the server checks for (Assets.cs). */
const HASH_LENGTH = 10;

/** Writes the stylesheet: the parts one after the other, with nothing added between them. */
async function sheet() {
  const parts = (await readdir(STYLES)).filter((f) => f.endsWith('.css')).sort();
  await mkdir(path.dirname(SHEET), { recursive: true });
  await writeFile(SHEET, (await Promise.all(parts.map((f) => readFile(path.join(STYLES, f), 'utf8')))).join(''));
}

const bundle = (watching) => ({
  entryPoints: { main: 'Scripts/main.ts', video: 'Scripts/video.ts' }, bundle: true, format: 'esm', target: 'es2022', sourcemap: true, minify: !watching,
  outdir: 'wwwroot/js',
});

if (process.argv.includes('--watch')) {
  await rm(LIST, { force: true });
  await sheet();
  watch(STYLES, () => void sheet());
  const watcher = await context(bundle(true));
  await watcher.watch();
} else {
  await sheet();
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
