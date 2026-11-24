/** A name or word for comparing: no accents, no capitals ("Đào" and "dao" are the same word for a person typing quickly). */
export const fold = (text: string): string => text.normalize('NFD').replace(/\p{M}/gu, '').replace(/đ/gi, 'd').toLocaleLowerCase('vi').trim();

/** Whether every word the person typed appears in the text, in any order. An empty search matches everything. */
export function matches(text: string, query: string): boolean {
  const words = fold(query).split(/\s+/).filter((w) => w !== '');
  const haystack = fold(text);
  return words.every((w) => haystack.includes(w));
}
