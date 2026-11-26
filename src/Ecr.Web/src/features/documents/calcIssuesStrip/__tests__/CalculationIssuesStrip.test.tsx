import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { issue, table } from '@/features/calculationIssues/__tests__/fixtures';
import type { FormulaErrorIssue } from '@/features/calculationIssues/api';
import type { MonthUnmatchedRows, UnmatchedRows } from '@/features/documents/useUnmatchedRows';
import { testTheme } from '@/test/render';
import { CalculationIssuesStrip } from '../CalculationIssuesStrip';
import { resetDetailsOpen } from '../calcIssuesStripModel';

/**
 * ⛔ b14 doc-notices-compact (людина 25.11, doc 96: три блоки «Calculation for <місяць>: some rows are not included» по
 * 3 рядки + технічні деталі — сітка з другого екрана): ОДНА смуга в рядок, згорнута; Details — матриця «причина × місяць».
 * Без фіксу червоні: смуги (`CalculationIssuesStrip`) не існує — на сторінці три панелі.
 *
 * ⚠ Каталог у тестах не завантажено — `t` дає позначений ключ `⟦ключ (параметри)⟧`.
 */
const Suffix = String(Date.now() % 100000);
const Sep = 209909;
const Oct = 209910;
const Nov = 209911;

function formulaError(row: number): FormulaErrorIssue {
  return {
    tableCode: 'T1',
    sheetCopyId: null,
    rowKey: `F-${Suffix}-${String(row)}`,
    rowNumber: row,
    columnCode: 'Total',
    columnHeaderL10n: { values: { en: 'Total' } },
    kind: 'Error',
    errorCode: '#DIV/0',
    formula: '[A] / [B]',
    inputs: [],
    source: null,
    since: '2099-11-13T10:00:00Z',
    rowLabelL10n: null,
    dependentCount: 0,
  };
}

function rowsOf(over: Partial<UnmatchedRows>): UnmatchedRows {
  return { issues: [], hints: [], finishedAt: '2099-11-13T11:41:00Z', formulaErrors: [], ...over };
}

/** Три місяці doc 96: вересень — помилки формул і бракує входів, жовтень — бракує входів і неактивні, листопад — бракує. */
const Months: readonly MonthUnmatchedRows[] = [
  { periodKey: Sep, rows: rowsOf({ formulaErrors: [formulaError(1), formulaError(2)], issues: [issue(1, 3, { kind: 'MissingInputs' })] }) },
  {
    periodKey: Oct,
    rows: rowsOf({ issues: [issue(1, 4, { kind: 'MissingInputs' }), issue(1, 5, { kind: 'MissingInputs' }), issue(1, 6, { kind: 'RowInactive', affectedCount: 3 })] }),
  },
  { periodKey: Nov, rows: rowsOf({ issues: [issue(1, 7, { kind: 'MissingInputs' })] }) },
];

function show(userId = 7, months: readonly MonthUnmatchedRows[] = Months, documentId = 96) {
  const onOpenCell = vi.fn();
  const view = render(
    <MantineProvider theme={testTheme}>
      <CalculationIssuesStrip userId={userId} memoryDocumentId={documentId} months={months} tables={[table(1, 'Stationary')]} onOpenCell={onOpenCell} />
    </MantineProvider>,
  );

  return { onOpenCell, view };
}

describe('CalculationIssuesStrip (b14 doc-notices-compact)', () => {
  beforeEach(() => {
    window.localStorage.clear();
    resetDetailsOpen();
  });
  afterEach(() => {
    window.localStorage.clear();
    vi.restoreAllMocks();
  });

  it('AC-2: одна згорнута смуга з сумами за причинами по всіх місяцях; тон — за найгіршою (помилка формули)', () => {
    show();

    expect(screen.getAllByTestId('document-calc-strip')).toHaveLength(1);
    const strip = screen.getByTestId('document-calc-strip');
    expect(strip.getAttribute('data-blocking')).toBe('true');
    expect(strip.getAttribute('data-months')).toBe(`${String(Sep)},${String(Oct)},${String(Nov)}`);
    const text = screen.getByTestId('document-calc-strip-text').textContent ?? '';
    expect(text).toContain('document.notices.calcTitleBlocked');
    // Блокувальна причина першою: 2 помилки формул · 4 рядки без входів · 3 неактивні.
    expect(text).toMatch(/2 ⟦document\.notices\.kind\.formula⟧ · 4 ⟦document\.notices\.kind\.missingInputs⟧ · 3 ⟦document\.notices\.kind\.inactive⟧/);
    // Згорнуто: ні матриці, ні панелей місяців (колишні три блоки).
    expect(screen.getByTestId('document-calc-strip-toggle').getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryAllByTestId('document-calc-issues')).toHaveLength(0);
  });

  it('AC-2: Details — матриця «причина × місяць»; «Show rows» веде на ту саму першу комірку причини, що й панель місяця', async () => {
    const { onOpenCell } = show();

    await userEvent.click(screen.getByTestId('document-calc-strip-toggle'));
    const matrix = await screen.findByTestId('document-calc-strip-matrix');
    const heads = [...matrix.querySelectorAll('thead th')].map((cell) => cell.getAttribute('data-period'));
    expect(heads).toEqual([null, String(Sep), String(Oct), String(Nov)]);

    const missing = matrix.querySelector('tr[data-kind="missingInputs"]') as HTMLElement;
    expect([...missing.querySelectorAll('td')].map((cell) => cell.getAttribute('data-count'))).toEqual(['1', '2', '1']);
    const inactive = matrix.querySelector('tr[data-kind="inactive"]') as HTMLElement;
    expect([...inactive.querySelectorAll('td')].map((cell) => cell.getAttribute('data-count'))).toEqual(['0', '3', '0']);

    // ⚠ ireEvent: Collapse у jsdom не знімає pointer-events, і userEvent клік відкидає.
    fireEvent.click(within(missing).getAllByTestId('document-calc-strip-show')[1]!);
    // Жовтень: перший рядок причини «бракує входів» — row-1-4 таблиці T1 (екземпляр 101).
    expect(onOpenCell).toHaveBeenCalledTimes(1);
    expect(onOpenCell.mock.calls[0]?.slice(0, 2)).toEqual([101, 'row-1-4']);

    // Технічні деталі — там же, згорнуті; розгорнуті — колишні панелі місяців.
    const technical = screen.getByTestId('document-calc-strip-technical-toggle');
    expect(technical.getAttribute('aria-expanded')).toBe('false');
    fireEvent.click(technical);
    expect(await screen.findAllByTestId('document-calc-issues')).toHaveLength(3);
  });

  it('DOCUX L1: Details пам’ятається лише для ЦЬОГО документа; інший документ відкривається згорнутим; localStorage не пишеться', async () => {
    const first = show(7, Months, 96);
    await userEvent.click(screen.getByTestId('document-calc-strip-toggle'));
    expect(window.localStorage.length).toBe(0);
    first.view.unmount();

    // Той самий документ (перемкнули аркуш і повернулись) — розгорнуто.
    const again = show(7, Months, 96);
    expect(screen.getByTestId('document-calc-strip-toggle').getAttribute('aria-expanded')).toBe('true');
    again.view.unmount();

    // Інший документ — згорнуто (доти — розгорнуто на кожному документі: пам'ять на людину).
    show(7, Months, 100);
    const toggle = screen.getByTestId('document-calc-strip-toggle');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    await userEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
  });

  it('AC-4: смуга — region з назвою; Details — кнопка з aria-expanded і aria-controls', () => {
    show();

    const region = screen.getByRole('region', { name: /document\.calcIssues\.listLabel/ });
    expect(region.getAttribute('data-testid')).toBe('document-calc-strip');
    const toggle = within(region).getByRole('button', { name: /document\.notices\.details/ });
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(document.getElementById(toggle.getAttribute('aria-controls') ?? '')).not.toBeNull();
  });

  it('один місяць без помилок формул — інформаційний тон і назва місяця замість «N months»', () => {
    show(7, [Months[2]!]);

    const strip = screen.getByTestId('document-calc-strip');
    expect(strip.getAttribute('data-blocking')).toBe('false');
    expect(screen.getByTestId('document-calc-strip-text').textContent).toContain('document.notices.calcTitle (months=');
  });

  it('проблем немає — смуги немає', () => {
    show(7, [{ periodKey: Sep, rows: rowsOf({}) }]);

    expect(screen.queryByTestId('document-calc-strip')).toBeNull();
  });
});
