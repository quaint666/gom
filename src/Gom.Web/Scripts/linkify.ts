/** A piece of a message: words, or an address that can be opened. */
export type Piece = { text: string } | { text: string; url: string };

const CANDIDATE = /(?:https?:\/\/|www\.)[^\s<>"]+/giu;
/** What often follows an address in a sentence but is not part of it. */
const TRAILING = /[.,;:!?'"”’»…]+$/u;

/** Takes the sentence's own punctuation off the end, and a closing bracket that has no opening one inside the address. */
function trim(candidate: string): string {
  let text = candidate;
  for (;;) {
    const before = text;
    text = text.replace(TRAILING, '');
    for (const [open, close] of [['(', ')'], ['[', ']'], ['{', '}']] as const) {
      if (text.endsWith(close) && count(text, close) > count(text, open)) text = text.slice(0, -1);
    }
    if (text === before) return text;
  }
}

const count = (text: string, ch: string): number => text.split(ch).length - 1;

/** The address to open for what was typed, or null when it must not be a link: only http and https, and nothing with a name and password in front of the host (a trick to make an address look like another one). */
export function safeUrl(text: string): string | null {
  const full = /^https?:\/\//iu.test(text) ? text : `https://${text}`;
  let url: URL;
  try { url = new URL(full); } catch { return null; }
  if (url.protocol !== 'https:' && url.protocol !== 'http:') return null;
  if (url.username !== '' || url.password !== '') return null;
  if (!url.hostname.includes('.')) return null;   // "http://localhost" or "https://x" is not something to send people to
  return url.href;
}

/** A message split into plain words and addresses, in order. Putting the pieces back together gives the message exactly. */
export function linkify(body: string): Piece[] {
  const pieces: Piece[] = [];
  let last = 0;
  for (const match of body.matchAll(CANDIDATE)) {
    const start = match.index;
    // "xwww.example.com" is a word that happens to contain "www.": leave it.
    if (start > 0 && /[\p{L}\p{N}]/u.test(body[start - 1]!) && !/^https?:/iu.test(match[0])) continue;
    const text = trim(match[0]);
    const url = safeUrl(text);
    if (url === null) continue;
    if (start > last) pieces.push({ text: body.slice(last, start) });
    pieces.push({ text, url });
    last = start + text.length;
  }
  if (last < body.length) pieces.push({ text: body.slice(last) });
  return pieces;
}
