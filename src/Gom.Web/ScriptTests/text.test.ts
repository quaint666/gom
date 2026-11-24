import { describe, expect, it } from 'vitest';
import { errorText } from '../Scripts/text.js';
import { fail } from './helpers.js';

describe('errorText', () => {
  it.each([
    'invalid_credentials', 'too_many_attempts', 'id_taken', 'invalid_gom_id', 'password_too_short', 'password_too_long',
    'password_too_common', 'invalid_display_name', 'missing_fields', 'network', 'server',
  ] as const)('has its own Vietnamese sentence for %s', (code) => {
    const sentence = errorText(fail(code));
    expect(sentence).toMatch(/[ạ-ỹàáâãèéêìíòóôõùúăđĩũơưÀ-Ỹ]| /); // real words, not the code
    expect(sentence).not.toContain('_');
  });

  it('gives every code a different sentence', () => {
    const codes = ['invalid_credentials', 'too_many_attempts', 'id_taken', 'invalid_gom_id', 'password_too_short', 'password_too_long', 'password_too_common', 'invalid_display_name', 'missing_fields', 'network', 'server'] as const;
    expect(new Set(codes.map((c) => errorText(fail(c)))).size).toBe(codes.length);
  });

  it('tells how long to wait', () => {
    expect(errorText(fail('too_many_attempts', 429, 12))).toContain('12');
  });

  it('never shows a raw code for something unknown', () => {
    expect(errorText(fail('unknown', 418))).toBe(errorText(fail('bad_origin', 403)));
    expect(errorText(fail('unknown', 418))).not.toContain('unknown');
  });

  it('the sign-in error does not say whether the ID exists', () => {
    expect(errorText(fail('invalid_credentials', 401))).toBe('Gom ID hoặc mật khẩu không đúng. Hãy thử lại.');
  });
});
