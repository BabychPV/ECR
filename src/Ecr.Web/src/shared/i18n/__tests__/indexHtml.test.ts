import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { DefaultLanguage } from '@/shared/i18n';

/**
 * A2-09 (= A1-20): `index.html` — оболонка, яку сервер віддає як є, а Vite переносить її коментарі в
 * `dist/index.html` без змін. Внутрішній коментар розробників про `lang` потрапив так у продакшн.
 * Пояснення живе біля `applyDocumentLanguage` (`shared/i18n/index.ts`), у розмітці — лише атрибут.
 */
const html = readFileSync(path.resolve(process.cwd(), 'index.html'), 'utf8');

describe('index.html (A2-09)', () => {
  it('не містить HTML-коментарів — вони дійшли б до продакшн-збірки', () => {
    expect(html).not.toContain('<!--');
  });

  it('стартова мова документа — мова за замовчуванням шару i18n', () => {
    expect(html).toMatch(new RegExp(`<html lang="${DefaultLanguage}">`));
  });
});
