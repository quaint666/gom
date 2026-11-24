import { describe, expect, it, vi } from 'vitest';
import { createApi } from '../Scripts/api.js';
import { errorText } from '../Scripts/text.js';
import { fail } from './helpers.js';

describe('api: recall', () => {
  it('uses DELETE on the message', async () => {
    const fetchFn = vi.fn(async () => new Response(null, { status: 204 }));
    const result = await createApi(fetchFn).recall('c1', 'm1');
    const [url, init] = fetchFn.mock.calls[0] as unknown as [string, RequestInit];
    expect([url, init.method]).toEqual(['/api/conversations/c1/messages/m1', 'DELETE']);
    expect(result.ok).toBe(true);
  });

  it('knows the new error code and says it in words', async () => {
    const api = createApi(async () => new Response(JSON.stringify({ error: 'not_your_message' }), { status: 403, headers: { 'Content-Type': 'application/json' } }));
    expect(await api.recall('c1', 'm1')).toMatchObject({ ok: false, error: 'not_your_message' });
    expect(errorText(fail('not_your_message'))).toContain('của chính bạn');
  });
});
