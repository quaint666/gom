import { describe, expect, it } from 'vitest';
import { clip, errorText } from '../Scripts/text.js';
import { limits } from '../Scripts/limits.js';
import { fail } from './helpers.js';

describe('errorText', () => {
  it.each([
    'invalid_credentials', 'too_many_attempts', 'id_taken', 'invalid_gom_id', 'password_too_short', 'password_too_long',
    'password_too_common', 'invalid_display_name', 'missing_fields', 'network', 'server', 'invalid_endpoint', 'invalid_keys',
  ] as const)('has its own Vietnamese sentence for %s', (code) => {
    const sentence = errorText(fail(code));
    expect(sentence).toContain(' '); // a sentence, not the code
    expect(sentence).not.toContain('_');
  });

  it('gives every code a different sentence', () => {
    const codes = ['invalid_credentials', 'too_many_attempts', 'id_taken', 'invalid_gom_id', 'password_too_short', 'password_too_long', 'password_too_common', 'invalid_display_name', 'missing_fields', 'network', 'server', 'invalid_endpoint', 'invalid_keys'] as const;
    expect(new Set(codes.map((c) => errorText(fail(c)))).size).toBe(codes.length);
  });

  it('tells how long to wait', () => {
    expect(errorText(fail('too_many_attempts', 429, 12))).toContain('12');
  });

  it('never shows a raw code for something unknown', () => {
    expect(errorText(fail('unknown', 418))).toBe(errorText(fail('forbidden', 403)));
    expect(errorText(fail('unknown', 418))).toBe('Có lỗi xảy ra. Hãy thử lại.');
    expect(errorText(fail('unknown', 418))).not.toContain('unknown');
  });

  it('the sign-in error does not say whether the ID exists', () => {
    expect(errorText(fail('invalid_credentials', 401))).toBe('Gom ID hoặc mật khẩu không đúng. Hãy thử lại.');
  });
});

describe('the server\'s limits in the sentences', () => {
  it('words each limit from what the page was sent', () => {
    expect(errorText(fail('invalid_gom_id'))).toContain('từ 3 đến 20 ký tự');
    expect(errorText(fail('message_too_long'))).toBe('Tin nhắn dài tối đa 4.000 ký tự.');
    expect(errorText(fail('too_large'))).toBe('Tệp quá lớn (ảnh tối đa 10 MB, tệp tối đa 25 MB).');
    expect(errorText(fail('invalid_invite'))).toBe('Lời mời chỉ có hiệu lực từ 1 đến 30 ngày và cho tối đa 100 người.');
  });

  it('follows a different limit when the server sends one', () => {
    const sent = document.body.dataset['limits']!;
    try {
      document.body.dataset['limits'] = JSON.stringify({ ...limits(), messageChars: 1234, pins: 5 });
      expect(errorText(fail('message_too_long'))).toBe('Tin nhắn dài tối đa 1.234 ký tự.');
      expect(errorText(fail('pin_limit'))).toBe('Chỉ ghim được tối đa 5 tin nhắn. Hãy bỏ ghim một tin trước.');
    } finally { document.body.dataset['limits'] = sent; }
  });

  it('says plainly when a page came without its limits', () => {
    const sent = document.body.dataset['limits']!;
    try {
      delete document.body.dataset['limits'];
      expect(() => limits()).toThrow('data-limits');
    } finally { document.body.dataset['limits'] = sent; }
  });
});

describe('clip', () => {
  it('leaves short words alone and cuts long ones with an ellipsis', () => {
    expect(clip('chào', 4)).toBe('chào');
    expect(clip('chào bạn', 4)).toBe('chào…');
  });

  it('counts a flag or a family as one character, as a person does', () => {
    expect(clip('🇻🇳🇻🇳', 1)).toBe('🇻🇳…');
    expect(clip('👨‍👩‍👧 hi', 2)).toBe('👨‍👩‍👧 …');
  });
});
