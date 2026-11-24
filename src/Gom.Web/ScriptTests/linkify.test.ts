import { describe, expect, it } from 'vitest';
import { linkify, safeUrl } from '../Scripts/linkify.js';

const urls = (body: string) => linkify(body).flatMap((p) => ('url' in p ? [p.url] : []));
const joined = (body: string) => linkify(body).map((p) => p.text).join('');

describe('linkify', () => {
  it('leaves plain words as they are', () => {
    expect(linkify('xin chào cả nhà')).toEqual([{ text: 'xin chào cả nhà' }]);
    expect(linkify('')).toEqual([]);
  });

  it('finds an address in the middle of a sentence and keeps the words around it', () => {
    expect(linkify('xem https://example.com/a?b=1#c nhé')).toEqual([
      { text: 'xem ' }, { text: 'https://example.com/a?b=1#c', url: 'https://example.com/a?b=1#c' }, { text: ' nhé' },
    ]);
  });

  it('does not take the full stop, comma or question mark that ends the sentence', () => {
    expect(urls('vào https://example.com.')).toEqual(['https://example.com/']);
    expect(urls('xem https://example.com/x, rồi https://example.org/y?')).toEqual(['https://example.com/x', 'https://example.org/y']);
    expect(urls('"https://example.com/q!"')).toEqual(['https://example.com/q']);
  });

  it('keeps a bracket that belongs to the address and drops one that closes the sentence', () => {
    expect(urls('(xem https://example.com/a)')).toEqual(['https://example.com/a']);
    expect(urls('https://en.wikipedia.org/wiki/Gom_(chat)')).toEqual(['https://en.wikipedia.org/wiki/Gom_(chat)']);
    expect(urls('(https://en.wikipedia.org/wiki/Gom_(chat))')).toEqual(['https://en.wikipedia.org/wiki/Gom_(chat)']);
  });

  it('links www addresses as https, and only when they start a word', () => {
    expect(urls('www.example.com/trang')).toEqual(['https://www.example.com/trang']);
    expect(urls('thuwww.example.com')).toEqual([]);
  });

  it('finds several, in order', () => {
    expect(urls('https://a.example/1 và http://b.example/2')).toEqual(['https://a.example/1', 'http://b.example/2']);
  });

  it('never links anything but http and https', () => {
    expect(urls('javascript:alert(1) và ftp://example.com và data:text/html,x và mailto:a@b.co')).toEqual([]);
    expect(safeUrl('javascript:alert(1)')).toBeNull();
    expect(safeUrl('file:///etc/passwd')).toBeNull();
  });

  it('refuses an address with a name and password in front of the host, which is how one site passes for another', () => {
    expect(urls('https://www.google.com@evil.example/login')).toEqual([]);
    expect(urls('https://user:pw@example.com')).toEqual([]);
  });

  it('refuses a host with no dot, and a bare scheme', () => {
    expect(urls('http://localhost:5080 và https://intranet và https://')).toEqual([]);
  });

  it('puts the message back exactly, whatever was or was not a link', () => {
    for (const body of ['a https://example.com/x). b', 'dòng 1\nhttps://example.com\ndòng 3', 'https://example.com/ ', '  www.example.com  ']) {
      expect(joined(body)).toBe(body);
    }
  });

  it('keeps accented letters and long queries in the address', () => {
    expect(urls('https://vi.wikipedia.org/wiki/Hà_Nội?x=1&y=2')).toEqual(['https://vi.wikipedia.org/wiki/H%C3%A0_N%E1%BB%99i?x=1&y=2']);
  });
});
