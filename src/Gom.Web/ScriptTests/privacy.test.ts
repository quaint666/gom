import { beforeEach, describe, expect, it, vi as mock } from 'vitest';
import { mountPrivacy } from '../Scripts/pages/privacy.js';
import { en } from '../Scripts/strings/en.js';
import { vi } from '../Scripts/strings/vi.js';
import { fail, fill, flush, ok } from './helpers.js';

let root: HTMLElement;
let contact: ReturnType<typeof mock.fn>;

beforeEach(() => {
  document.body.replaceChildren();
  root = document.createElement('main');
  document.body.append(root);
  contact = mock.fn(async () => ok(undefined));
});

const mount = (operator: string) => mountPrivacy(root, operator, { contact: contact as never });

describe('the privacy policy', () => {
  it('links to the source code when there is one, and says nothing of it when not', () => {
    mountPrivacy(root, '', { contact: contact as never }, 'https://github.com/quaint666/gom');
    expect(root.querySelector('[data-source] a')?.getAttribute('href')).toBe('https://github.com/quaint666/gom');
    expect(root.querySelector('[data-source]')?.textContent).toContain('mã nguồn mở');
    root.replaceWith(root = document.createElement('main'));
    document.body.append(root);
    mount('');
    expect(root.querySelector('[data-source]')).toBeNull();
  });

  it('names who runs this Gom, as the server was configured', () => {
    mount('  Nguyễn An  ');
    expect(root.querySelector('[data-policy-intro]')?.textContent).toContain('do Nguyễn An vận hành');
  });

  it('says "the person who runs this site" when nobody is configured', () => {
    mount(' ');
    expect(root.querySelector('[data-policy-intro]')?.textContent).toContain('do người quản trị trang này vận hành');
  });

  it('shows the operator as text, never as markup', () => {
    mount('<b>An</b>');
    expect(root.querySelector('[data-policy-intro] b')).toBeNull();
    expect(root.querySelector('[data-policy-intro]')?.textContent).toContain('<b>An</b>');
  });

  it('dates itself in the page language', () => {
    mount('');
    expect(root.querySelector('[data-policy-date]')?.textContent).toBe('Cập nhật ngày 26 tháng 11, 2026');
  });

  it('has every part, each a heading with its points', () => {
    mount('');
    expect([...root.querySelectorAll('section h2')].map((h) => h.textContent)).toEqual([
      'Gom lưu những gì', 'Ai thấy được', 'Dịch vụ bên ngoài', 'Giữ an toàn', 'Lưu bao lâu và xóa thế nào', 'Quyền của bạn', 'Khi chính sách thay đổi', 'Liên hệ',
    ]);
    expect(root.querySelectorAll('section li').length).toBe(vi.privacy.sections.flatMap((s) => s.items).length);
  });

  it('leads back to Gom with a button at the top and one at the foot, after the contact form', () => {
    mount('');
    const top = root.querySelector<HTMLAnchorElement>('a.btn[data-home-top]')!;
    const foot = root.querySelector<HTMLAnchorElement>('a.btn[data-home]')!;
    for (const a of [top, foot]) {
      expect(a.getAttribute('href')).toBe('/');
      expect(a.textContent?.trim()).toBe('Về trang chính');
      expect(a.querySelector('svg')).not.toBeNull();
    }
    expect(root.querySelector('[data-privacy]')!.firstElementChild).toBe(top);
    expect(root.querySelector('[data-contact]')!.compareDocumentPosition(foot) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(en.privacy.home).toBe('Return to Gom');
    expect(en.account.toHome).toBe('Return to Gom');
  });

  it('says the same number of things in English, part by part', () => {
    expect(en.privacy.sections.map((s) => s.items.length)).toEqual(vi.privacy.sections.map((s) => s.items.length));
  });
});

describe('writing to the operator', () => {
  const send = async () => { root.querySelector<HTMLFormElement>('[data-contact-form]')!.requestSubmit(); await flush(); };

  it('says how long a message is kept, as the server says', () => {
    mount('');
    expect(root.querySelector('[data-contact]')?.textContent).toContain('tự xóa sau 180 ngày');
  });

  it('sends the message, the way to reach the writer and the hidden field, then thanks them', async () => {
    mount('');
    fill(root, '#contact-body', 'Xin xóa ảnh cũ của tôi');
    fill(root, '#contact-reply', 'an@example.com');
    await send();
    expect(contact).toHaveBeenCalledWith('Xin xóa ảnh cũ của tôi', 'an@example.com', '');
    expect(root.querySelector('[data-contact-sent]')?.textContent).toBe('Đã gửi. Cảm ơn bạn.');
    expect(root.querySelector('[data-contact-form]')).toBeNull();
  });

  it('keeps the form and says why when the server refuses', async () => {
    contact.mockResolvedValueOnce(fail('contact_full', 429));
    mount('');
    fill(root, '#contact-body', 'Chào');
    await send();
    expect(root.querySelector('[role="alert"]')?.textContent).toContain('Hôm nay Gom đã nhận nhiều thư');
    expect(root.querySelector<HTMLTextAreaElement>('#contact-body')?.value).toBe('Chào');
  });

  it('hides the bot field from people and from the keyboard', () => {
    mount('');
    const trap = root.querySelector<HTMLInputElement>('#contact-website')!;
    expect(trap.closest('[aria-hidden="true"]')?.classList.contains('sr-only')).toBe(true);
    expect(trap.tabIndex).toBe(-1);
  });
});
