import { beforeEach, describe, expect, it } from 'vitest';
import { mountPrivacy } from '../Scripts/pages/privacy.js';
import { en } from '../Scripts/strings/en.js';
import { vi } from '../Scripts/strings/vi.js';

let root: HTMLElement;

beforeEach(() => {
  document.body.replaceChildren();
  root = document.createElement('main');
  document.body.append(root);
});

describe('the privacy policy', () => {
  it('names who runs this Gom, as the server was configured', () => {
    mountPrivacy(root, '  Nguyễn An  ');
    expect(root.querySelector('[data-policy-intro]')?.textContent).toContain('do Nguyễn An vận hành');
  });

  it('says "the person who runs this site" when nobody is configured', () => {
    mountPrivacy(root, ' ');
    expect(root.querySelector('[data-policy-intro]')?.textContent).toContain('do người quản trị trang này vận hành');
  });

  it('shows the operator as text, never as markup', () => {
    mountPrivacy(root, '<b>An</b>');
    expect(root.querySelector('[data-policy-intro] b')).toBeNull();
    expect(root.querySelector('[data-policy-intro]')?.textContent).toContain('<b>An</b>');
  });

  it('dates itself in the page language', () => {
    mountPrivacy(root, '');
    expect(root.querySelector('[data-policy-date]')?.textContent).toBe('Cập nhật ngày 26 tháng 11, 2026');
  });

  it('has every part, each a heading with its points', () => {
    mountPrivacy(root, '');
    expect([...root.querySelectorAll('section h2')].map((h) => h.textContent)).toEqual([
      'Gom lưu những gì', 'Ai thấy được', 'Dịch vụ bên ngoài', 'Giữ an toàn', 'Lưu bao lâu và xóa thế nào', 'Quyền của bạn', 'Khi chính sách thay đổi',
    ]);
    expect(root.querySelectorAll('section li').length).toBe(vi.privacy.sections.flatMap((s) => s.items).length);
  });

  it('says the same number of things in English, part by part', () => {
    expect(en.privacy.sections.map((s) => s.items.length)).toEqual(vi.privacy.sections.map((s) => s.items.length));
  });
});
