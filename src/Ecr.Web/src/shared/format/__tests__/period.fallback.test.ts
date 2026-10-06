import { describe, expect, it } from 'vitest';
import { setLanguage } from '@/shared/i18n';
import { formatMonthYear } from '@/shared/format';

/**
 * `A2-10`, запасний шлях: каталог ще не прийшов (перший рендер) — підпис
 * будує `Intl`, а не показує `⟦periods.monthOf⟧`. Мова без даних ICU
 * (`xx`, як `kk` у Chrome) не кидає: `formatLocale()` відкочується на en.
 */
describe('formatMonthYear без каталогу', () => {
  it('en — «September 2026»', () => {
    setLanguage('en');

    expect(formatMonthYear(2026, 9)).toBe('September 2026');
  });

  it('невідома ICU мова — не виняток, а мова за замовчуванням', () => {
    setLanguage('xx');

    expect(() => formatMonthYear(2026, 9)).not.toThrow();
    expect(formatMonthYear(2026, 9)).toBe('September 2026');

    setLanguage('en');
  });
});
