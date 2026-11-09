import { describe, expect, it } from 'vitest';
import { fireEvent, screen } from '@testing-library/react';
import type { ReactNode } from 'react';
import { MemoryRouter } from 'react-router-dom';
import { renderWithMantine } from '@/test/render';
import { RecalcSkippedList, skippedSheetHref, type SkippedSheet } from '@/features/workflow/RecalcSkippedList';
import { sheetTabKey } from '@/features/documents/sheetCopies/sheetCopyTypes';

/**
 * ✎ UAT-recalc-ux: після перерахунку людина бачить, які ЗАТВЕРДЖЕНІ аркуші пропущено, і переходить до кожного —
 * документ, аркуш і період у посиланні; «рахувати нічого» — пояснення з тим самим переліком, а не помилка.
 */
const sheet = (over: Partial<SkippedSheet> = {}): SkippedSheet => ({
  documentId: 89,
  documentCode: 'LAND-PROD-89',
  documentName: { values: { en: 'Land 89' } },
  sheetDefId: 161,
  sheetCode: 'S161',
  sheetName: { values: { en: 'Emissions' } },
  periodKey: 202602,
  state: 'Approved',
  ...over,
});

const renderWithProviders = (ui: ReactNode) => renderWithMantine(<MemoryRouter>{ui}</MemoryRouter>);

describe('RecalcSkippedList', () => {
  it('нічого не пропущено і є задача — нічого не показує', () => {
    const { container } = renderWithProviders(<RecalcSkippedList outcome={{ jobId: 'j1', skipped: [] }} />);
    expect(container.querySelector('[data-recalc-skipped]')).toBeNull();
  });

  it('пропущені — кількість і посилання на документ, аркуш і період', () => {
    const { container } = renderWithProviders(
      <RecalcSkippedList
        outcome={{ jobId: 'j1', skipped: [sheet(), sheet({ periodKey: 202603, documentName: null })] }}
      />,
    );

    const summary = container.querySelector('summary');
    expect(summary?.textContent).toContain('2');
    fireEvent.click(summary as Element);
    const links = screen.getAllByRole('link');
    expect(links).toHaveLength(2);
    expect(links[0]?.textContent).toBe('Land 89 · Emissions · 202602');
    expect(links[0]?.getAttribute('href')).toBe('/documents/89?periodKey=202602&sheet=S161');
    // Без назви документа — його код.
    expect(links[1]?.textContent).toBe('LAND-PROD-89 · Emissions · 202603');
  });

  it('рахувати нічого — пояснення з переліком, не помилка', () => {
    const { container } = renderWithProviders(
      <RecalcSkippedList outcome={{ jobId: null, nothingToRecalculate: true, skipped: [sheet()], skippedTruncated: true }} />,
    );

    expect(container.querySelector('[data-recalc-nothing]')).not.toBeNull();
    expect(container.querySelectorAll('[data-recalc-skipped-link]')).toHaveLength(1);
  });

  // ✎ Борг 09.11. Мутація: брати `sheetName` замість `sheetDisplayName` або код без копії в адресі — падає.
  it('затверджена копія — назва «Vessel 2» і посилання на вкладку копії', () => {
    const { container } = renderWithProviders(
      <RecalcSkippedList
        outcome={{
          jobId: 'j1',
          skipped: [
            sheet({ sheetName: { values: { en: 'Vessel' } }, sheetDisplayName: { values: { en: 'Vessel 1' } } }),
            sheet({
              sheetName: { values: { en: 'Vessel' } },
              sheetDisplayName: { values: { en: 'Vessel 2' } },
              sheetCopyId: 77,
            }),
          ],
        }}
      />,
    );

    fireEvent.click(container.querySelector('summary') as Element);
    const links = screen.getAllByRole('link');
    expect(links.map((l) => l.textContent)).toEqual(['Land 89 · Vessel 1 · 202602', 'Land 89 · Vessel 2 · 202602']);
    expect(links[1]?.getAttribute('href')).toBe(
      `/documents/89?${new URLSearchParams({ periodKey: '202602', sheet: sheetTabKey('S161', 77) }).toString()}`,
    );
    expect(links[1]?.getAttribute('href')).not.toBe(links[0]?.getAttribute('href'));
  });

  it('адреса кодує код аркуша', () => {
    expect(skippedSheetHref({ documentId: 5, periodKey: 202601, sheetCode: 'A B' })).toBe(
      '/documents/5?periodKey=202601&sheet=A+B',
    );
  });
});
