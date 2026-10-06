import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { loadCatalog, setLanguage } from '@/shared/i18n';
import { PeriodPicker } from '@/shared/ui/PeriodPicker';
import { renderWithMantine } from '@/test/render';

/**
 * Поле вибору періоду показує ЛЮДСЬКУ назву місяця мовою інтерфейсу («October 2026» /
 * «Октябрь 2026» / «Қазан 2026»), а не технічний ключ `202610`, і без дублювання підписом.
 * Ключ лишається значенням для API (`onChange`) і видно його лише при редагуванні.
 * Форматер — той самий, що в `A2-10` (`formatMonthYear` через `formatPeriodKey`).
 */

const Catalogs: Record<string, Record<string, string>> = {
  ru: { 'periods.monthOf': '{month} {year}', 'periods.month.10': 'Октябрь' },
  kz: { 'periods.monthOf': '{month} {year}', 'periods.month.10': 'Қазан' },
};

beforeAll(async () => {
  for (const lang of ['ru', 'kz']) {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          new Response(JSON.stringify({ languageCode: lang, revision: 1, strings: Catalogs[lang] }), {
            status: 200,
            headers: { 'Content-Type': 'application/json', ETag: `"public-${lang}-1"` },
          }),
        ),
      ),
    );
    await loadCatalog(lang, 'public');
    vi.unstubAllGlobals();
  }
});

afterEach(() => {
  cleanup();
  setLanguage('en');
});

afterAll(() => {
  setLanguage('en');
  localStorage.clear();
});

const field = (): HTMLInputElement =>
  screen.getByRole<HTMLInputElement>('textbox', { name: '⟦documents.period⟧' });

describe('PeriodPicker: людська назва в полі', () => {
  it.each([
    ['en', 'October 2026'],
    ['ru', 'Октябрь 2026'],
    ['kz', 'Қазан 2026'],
  ])('%s: поле показує «%s», а не 202610', (language, expected) => {
    setLanguage(language);
    renderWithMantine(<PeriodPicker value={202610} onChange={vi.fn()} />);

    expect(field().value).toBe(expected);
    expect(screen.queryByDisplayValue('202610')).toBeNull();
    // Без дублювання: назва лише в полі, окремого підпису-тексту немає.
    expect(screen.queryByText(expected)).toBeNull();
  });

  it('ключ лишається значенням: набір YYYYMM віддає число в onChange', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderWithMantine(<PeriodPicker value={202610} onChange={onChange} />);

    await user.click(field());
    // У фокусі поле показує ключ для редагування.
    expect(field().value).toBe('202610');

    await user.type(field(), '202611', { initialSelectionStart: 0, initialSelectionEnd: 6 });
    expect(onChange).toHaveBeenLastCalledWith(202611);

    await user.tab();
    expect(field().value).toBe('November 2026');
  });

  it('клавіатура: Tab веде ‹ → поле → ›, у полі стає видно ключ', async () => {
    const user = userEvent.setup();
    renderWithMantine(<PeriodPicker value={202610} onChange={vi.fn()} />);

    await user.tab();
    expect(document.activeElement).toBe(screen.getByRole('button', { name: '⟦period.previous⟧' }));
    await user.tab();
    expect(document.activeElement).toBe(field());
    expect(field().value).toBe('202610');
    await user.tab();
    expect(document.activeElement).toBe(screen.getByRole('button', { name: '⟦period.next⟧' }));
  });

  it('‹ › мають висоту поля (input-xs) і aria-label — вирівняні по центру поля', () => {
    renderWithMantine(<PeriodPicker value={202610} onChange={vi.fn()} />);

    for (const name of ['⟦period.previous⟧', '⟦period.next⟧']) {
      expect(screen.getByRole('button', { name }).getAttribute('data-size')).toBe('input-xs');
    }
  });
});
