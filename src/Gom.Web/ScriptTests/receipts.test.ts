import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createApi } from '../Scripts/api.js';
import { mountSettings } from '../Scripts/pages/settings.js';
import { fakeApi, flush, ok, text } from './helpers.js';

let root: HTMLElement;
beforeEach(() => { root = document.createElement('div'); document.body.replaceChildren(root); });

describe('api: read receipts', () => {
  it('reads who has read how far, and switches the setting with PATCH', async () => {
    const fetchFn = vi.fn(async () => new Response(JSON.stringify({ enabled: true, reads: [] }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    const api = createApi(fetchFn);
    await api.reads('c1');
    await api.setReceipts(false);
    const calls = fetchFn.mock.calls.map((c) => c as unknown as [string, RequestInit]);
    expect([calls[0]![0], calls[0]![1].method]).toEqual(['/api/conversations/c1/reads', 'GET']);
    expect([calls[1]![0], calls[1]![1].method, JSON.parse(calls[1]![1].body as string)]).toEqual(['/api/me/receipts', 'PATCH', { enabled: false }]);
  });
});

describe('settings: read receipts', () => {
  const me = { id: 'u-mom', gomId: 'mom', displayName: 'Mẹ', isOwner: false, readReceipts: true };
  const api = (extra = {}, user = me) => fakeApi({ me: vi.fn(async () => ok(user)), sessions: vi.fn(async () => ok([])), ...extra });
  const box = () => root.querySelector<HTMLInputElement>('input[name="receipts"]')!;

  it('shows the switch as it is, with a label, and says it works both ways', async () => {
    mountSettings(root, api(), vi.fn());
    await flush();
    expect(box().checked).toBe(true);
    expect(root.querySelector(`label[for="${box().id}"]`)).not.toBeNull();
    expect(text(root)).toContain('bạn cũng không thấy');
  });

  it('treats a setting the server did not mention as on', async () => {
    const { readReceipts: _omitted, ...withoutIt } = me;
    mountSettings(root, api({}, withoutIt as typeof me), vi.fn());
    await flush();
    expect(box().checked).toBe(true);
  });

  it('shows it off when it is off', async () => {
    mountSettings(root, api({}, { ...me, readReceipts: false }), vi.fn());
    await flush();
    expect(box().checked).toBe(false);
  });

  it('changes the setting when switched, and says it was saved', async () => {
    const a = api();
    mountSettings(root, a, vi.fn());
    await flush();
    box().checked = false;
    box().dispatchEvent(new Event('change', { bubbles: true }));
    await flush();
    expect(a.setReceipts).toHaveBeenCalledWith(false);
    expect(text(root.querySelector('[data-receipts-status]')!)).toContain('Đã lưu');
    expect(box().checked).toBe(false);
  });

  it('puts the switch back and says why when it could not be saved', async () => {
    const a = api({ setReceipts: vi.fn(async () => ({ ok: false, status: 500, error: 'server' })) });
    mountSettings(root, a, vi.fn());
    await flush();
    box().checked = false;
    box().dispatchEvent(new Event('change', { bubbles: true }));
    await flush();
    expect(box().checked).toBe(true);
    expect(root.querySelector('[role="alert"]')).not.toBeNull();
  });
});
