/** Text for comparing, character by character: no accents, no capitals ("Đào" and "dao" are the same word for a person typing quickly). */
const unmark = (text: string): string => text.normalize('NFD').replace(/\p{M}/gu, '').replace(/đ/gi, 'd').toLocaleLowerCase('vi');

/** A name or word for comparing: no accents, no capitals, no spaces around it. */
export const fold = (text: string): string => unmark(text).trim();

/** The words of a search, each folded. */
export const searchWords = (query: string): string[] => fold(query).split(/\s+/).filter((w) => w !== '');

/** Whether every word the person typed appears in the text, in any order. An empty search matches everything. */
export function matches(text: string, query: string): boolean {
  const haystack = fold(text);
  return searchWords(query).every((w) => haystack.includes(w));
}

/** A piece of a text, and whether it is one of the words searched for. */
export interface Run { text: string; hit: boolean }

/**
 * The text cut into runs, with every place a word of the search appears marked, ignoring capitals and accents as the search does
 * ("pho" marks "Phở"). Joined back together, the runs are the text exactly.
 */
export function markWords(text: string, query: string): Run[] {
  const words = searchWords(query);
  const points = [...text];
  // Each character's folded form, and where it starts in the folded text: a character can fold to none (a lone accent) or several.
  const starts: number[] = [];
  let folded = '';
  for (const p of points) { starts.push(folded.length); folded += unmark(p); }
  const hitAt = new Array<boolean>(folded.length).fill(false);
  for (const w of words) for (let i = folded.indexOf(w); i !== -1; i = folded.indexOf(w, i + 1)) hitAt.fill(true, i, i + w.length);

  const runs: Run[] = [];
  points.forEach((p, i) => {
    const end = i + 1 < points.length ? starts[i + 1]! : folded.length;
    // A character that folds to nothing (an accent on its own) goes with the one before it.
    const hit = end > starts[i]! ? hitAt.slice(starts[i], end).some(Boolean) : runs.at(-1)?.hit ?? false;
    const last = runs.at(-1);
    if (last !== undefined && last.hit === hit) last.text += p;
    else runs.push({ text: p, hit });
  });
  return runs;
}

/**
 * A long text cut down to about <paramref name="chars"/> characters around the first word found, with "…" where something was left
 * out, then marked: a word near the end of a long message still shows.
 */
export function markedExcerpt(text: string, query: string, chars: number): Run[] {
  const graphemes = [...new Intl.Segmenter(undefined, { granularity: 'grapheme' }).segment(text)].map((s) => s.segment);
  if (graphemes.length <= chars) return markWords(text, query);
  const runs = markWords(text, query);
  const firstHit = runs.findIndex((r) => r.hit);
  const offset = firstHit < 0 ? 0 : runs.slice(0, firstHit).reduce((n, r) => n + r.text.length, 0);
  // The grapheme the first word starts in, with some of what comes before it.
  let at = 0;
  for (let seen = 0; at < graphemes.length && seen + graphemes[at]!.length <= offset; at++) seen += graphemes[at]!.length;
  const start = Math.max(0, Math.min(at - Math.min(LEAD, Math.floor(chars / 3)), graphemes.length - chars));
  const end = Math.min(graphemes.length, start + chars);
  const cut = (start > 0 ? '…' : '') + graphemes.slice(start, end).join('') + (end < graphemes.length ? '…' : '');
  return markWords(cut, query);
}

/** How many characters before the first word found an excerpt keeps (at most a third of it), so the word is read in its sentence. */
const LEAD = 30;
