import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { issue, table } from '@/features/calculationIssues/__tests__/fixtures';
import type { FormulaErrorIssue } from '@/features/calculationIssues/api';
import { CalculationIssuesPanel } from '@/features/documents/CalculationIssuesPanel';
import type { UnmatchedRows } from '@/features/documents/useUnmatchedRows';
import { testTheme } from '@/test/render';

/**
 * ✎ B78 (людина 13.11: «не юзер френдлі, багато тексту»; погоджений макет `B78-calc-issues-mockup.md`): ОДНА панель
 * замість двох банерів — рядок на ПРИЧИНУ з бейджем і людськими назвами; формули й коди — лише в згорнутих
 * «Технічних деталях»; «Відкрити HSE400» лише для причини HSE400; «Показати клітинки/рядки» відкриває першу.
 *
 * ⚠ Каталог у тестах не завантажено — `t` дає позначений ключ `⟦ключ (параметри)⟧`.
 */

const Formula =
  "REGFIELD(REGFIND('LAND_HSE400_COMPOSITION', [Period].Year, [Period].Sequence, REGFIELD(REGFIND('LAND_HSE400_STREAM', REGFIELD([Land_TypeFuel], 'NAME')), 'PREFIX'), 'Density20'), 'VALUE')";

/** Land doc 96: склад HSE400 відсутній — #N/A; паливо вже назвою (сервер, B78). */
function hse400(row: number, column: string, header: string, fuel: string): FormulaErrorIssue {
  return {
    tableCode: 'T1',
    sheetCopyId: null,
    rowKey: `LTD-A0${String(row)}`,
    rowNumber: row,
    columnCode: column,
    columnHeaderL10n: { values: { en: header } },
    kind: 'Error',
    errorCode: '#N/A',
    formula: Formula,
    inputs: [{ tableCode: 'T1', rowKey: `LTD-A0${String(row)}`, columnCode: 'Land_TypeFuel', periodOffset: 0, value: fuel }],
    source: null,
    since: '2026-11-13T10:00:00Z',
    rowLabelL10n: null,
    dependentCount: 0,
  };
}

function rowsOf(over: Partial<UnmatchedRows>): UnmatchedRows {
  return { issues: [], hints: [], finishedAt: '2026-11-13T11:41:00Z', formulaErrors: [], ...over };
}

function show(rows: UnmatchedRows, onOpenHse400?: () => Promise<boolean>) {
  const onOpenCell = vi.fn();
  render(
    <MantineProvider theme={testTheme}>
      <CalculationIssuesPanel
        userId={7}
        periodKey={202610}
        rows={rows}
        tables={[table(1, '3.00 Stationary equipment')]}
        onOpenCell={onOpenCell}
        onOpenHse400={onOpenHse400}
      />
    </MantineProvider>,
  );

  return onOpenCell;
}

describe('CalculationIssuesPanel (B78)', () => {
  beforeEach(() => window.localStorage.clear());
  afterEach(() => window.localStorage.clear());

  it('HSE400: усі комірки — ОДИН рядок причини з бейджем, паливом назвою і показниками; формул не видно', () => {
    show(
      rowsOf({
        formulaErrors: [
          hse400(1, 'Density', 'Gas density', 'Diesel'),
          hse400(2, 'Density', 'Gas density', 'Diesel'),
          hse400(3, 'Density', 'Gas density', 'Gasoline'),
          { ...hse400(3, 'Heat', 'Lower heating value', 'Gasoline'), dependentCount: 2 },
        ],
      }),
      () => Promise.resolve(true),
    );

    const panel = screen.getByTestId('document-calc-issues');
    expect(panel.getAttribute('data-blocking')).toBe('true');
    expect(screen.getByTestId('document-calc-issues-title').textContent).toContain('document.calcIssues.titleBlocked');

    const causes = screen.getAllByTestId('document-calc-issue');
    expect(causes).toHaveLength(1);
    expect(causes[0]!.getAttribute('data-kind')).toBe('hse400');
    // 4 власні помилки + 2 не оновлені залежні.
    expect(screen.getByTestId('document-calc-issue-count').textContent).toContain('count=6');
    expect(screen.getByTestId('document-calc-issue-title').textContent).toContain('document.calcIssues.hse400.title (count=2)');
    const detail = screen.getByTestId('document-calc-issue-detail').textContent ?? '';
    expect(detail).toContain('Diesel, Gasoline');
    expect(detail).toContain('names=Gas density, Lower heating value');
    expect(detail).toContain('Sheet · 3.00 Stationary equipment');

    // Технічні деталі — згорнуті; формула лише там.
    expect(screen.getByTestId('document-calc-issues-details-toggle').getAttribute('aria-expanded')).toBe('false');
    expect(Array.from(panel.querySelectorAll('[data-testid="document-calc-issue"]')).some((c) => c.textContent?.includes('REGFIELD'))).toBe(false);
    expect(screen.getByTestId('document-calc-issue-open-hse400')).toBeDefined();
  });

  it('✎ F13 P2 (doc 96, жовтень): «паливо» — лише назви записів довідника, без текстових входів пошуку', () => {
    // Форма doc 96: T3_00 читає довідникову [Land_TypeFuel] (назвою — сервер), інші таблиці — текстову [TypeOfStreams].
    const streams = (row: number, stream: string): FormulaErrorIssue => ({
      ...hse400(row, 'Density0', 'Gas density at 0', stream),
      formula:
        "REGFIELD(REGFIND('LAND_HSE400_COMPOSITION', [Period].Year, [Period].Sequence, REGFIELD(REGFIND('LAND_HSE400_STREAM', [TypeOfStreams]), 'PREFIX'), 'Density0'), 'VALUE')",
      inputs: [{ tableCode: 'T1', rowKey: `S${String(row)}`, columnCode: 'TypeOfStreams', periodOffset: 0, value: stream }],
    });
    show(
      rowsOf({
        formulaErrors: [
          hse400(1, 'Density', 'Gas density', 'Diesel'),
          hse400(2, 'Density', 'Gas density', 'Gasoline'),
          // Довідниковий Id, який сервер назвою не підтвердив, — не «паливо».
          hse400(3, 'Density', 'Gas density', '39'),
          streams(4, 'Makat Gas - Макатский газ'),
          streams(5, 'Fuel Gas - Топливный газ'),
          {
            ...hse400(6, 'Density', 'Gas density', 'Diesel'),
            inputs: [
              { tableCode: 'T1', rowKey: 'LTD-A06', columnCode: 'Land_TypeFuel', periodOffset: 0, value: 'Diesel' },
              { tableCode: 'T1', rowKey: 'LTD-A06', columnCode: 'Stream', periodOffset: 0, value: 'Diesel - Дизель' },
            ],
          },
        ],
      }),
    );

    expect(screen.getByTestId('document-calc-issue-title').textContent).toContain('document.calcIssues.hse400.title (count=2)');
    const detail = screen.getByTestId('document-calc-issue-detail').textContent ?? '';
    expect(detail).toContain('Diesel, Gasoline');
    expect(detail).not.toContain('Makat');
    expect(detail).not.toContain('Дизель');
    expect(detail).not.toContain('39');
  });

  it('«Показати клітинки» відкриває першу комірку причини і розгортає деталі', async () => {
    const onOpenCell = show(rowsOf({ formulaErrors: [hse400(2, 'Density', 'Gas density', 'Diesel')] }));

    await userEvent.click(screen.getByTestId('document-calc-issue-show'));

    expect(onOpenCell).toHaveBeenCalledWith(101, 'LTD-A02', 'Density');
    expect(screen.getByTestId('document-calc-issues-details-toggle').getAttribute('aria-expanded')).toBe('true');
    // Без обробника «Відкрити HSE400» кнопки немає.
    expect(screen.queryByTestId('document-calc-issue-open-hse400')).toBeNull();
  });

  it('«Відкрити HSE400»: документа немає — панель каже про це', async () => {
    const open = vi.fn(() => Promise.resolve(false));
    show(rowsOf({ formulaErrors: [hse400(1, 'Density', 'Gas density', 'Diesel')] }), open);

    await userEvent.click(screen.getByTestId('document-calc-issue-open-hse400'));

    expect(open).toHaveBeenCalledTimes(1);
    await waitFor(() => {
      expect(screen.getByTestId('document-calc-issue-hse400-missing').textContent).toContain('year=2026');
    });
  });

  it('✎ F13 P3-2: «Відкрити HSE400» не вдалося (відмова API) — панель показує помилку', async () => {
    const open = vi.fn(() => Promise.reject(new Error('503')));
    show(rowsOf({ formulaErrors: [hse400(1, 'Density', 'Gas density', 'Diesel')] }), open);

    await userEvent.click(screen.getByTestId('document-calc-issue-open-hse400'));

    await waitFor(() => {
      expect(screen.getByTestId('document-calc-issue-hse400-failed').textContent).toContain(
        'document.calcIssues.hse400OpenFailed',
      );
    });
    expect(screen.queryByTestId('document-calc-issue-hse400-missing')).toBeNull();
  });

  it('✎ F13 P3-3: сервер обрізав перелік (formulaErrorTotal більший за покриті записами комірки) — бейдж «N+»', () => {
    show(
      rowsOf({
        formulaErrors: [hse400(1, 'Density', 'Gas density', 'Diesel'), { ...hse400(2, 'Density', 'Gas density', 'Diesel'), dependentCount: 1 }],
        formulaErrorTotal: 1200,
      }),
    );

    const badge = screen.getByTestId('document-calc-issue-count');
    expect(badge.getAttribute('data-at-least')).toBe('true');
    expect(badge.textContent).toContain('document.calcIssues.cells.other (count=3+)');
  });

  it('✎ F13 P3-3: перелік повний (сума записів = formulaErrorTotal) — точна кількість без «+»', () => {
    show(
      rowsOf({
        formulaErrors: [hse400(1, 'Density', 'Gas density', 'Diesel'), { ...hse400(2, 'Density', 'Gas density', 'Diesel'), dependentCount: 1 }],
        formulaErrorTotal: 3,
      }),
    );

    const badge = screen.getByTestId('document-calc-issue-count');
    expect(badge.getAttribute('data-at-least')).toBeNull();
    expect(badge.textContent).toContain('(count=3)');
  });

  it('рядки поза розрахунком — причина «рядки», інформаційний тон, «Показати рядки» відкриває перший', async () => {
    const onOpenCell = show(rowsOf({ issues: [issue(1, 2), issue(1, 5)] }));

    expect(screen.getByTestId('document-calc-issues').getAttribute('data-blocking')).toBe('false');
    const cause = screen.getByTestId('document-calc-issue');
    expect(cause.getAttribute('data-kind')).toBe('unmatched');
    expect(cause.getAttribute('data-count')).toBe('2');
    expect(screen.queryByTestId('document-calc-issue-open-hse400')).toBeNull();

    await userEvent.click(screen.getByTestId('document-calc-issue-show'));
    expect(onOpenCell).toHaveBeenCalledWith(101, 'row-1-2', null);
  });

  it('інша помилка формули — окрема причина з людською назвою коду; помилки перед рядками', () => {
    show(
      rowsOf({
        issues: [issue(1, 2)],
        formulaErrors: [
          { ...hse400(1, 'Ratio', 'Ratio', '0'), formula: '[A] / [B]', errorCode: '#DIV/0', inputs: [] },
          hse400(2, 'Density', 'Gas density', 'Diesel'),
        ],
      }),
    );

    expect(screen.getAllByTestId('document-calc-issue').map((c) => c.getAttribute('data-kind'))).toEqual([
      'hse400',
      'formula',
      'unmatched',
    ]);
    expect(screen.getAllByTestId('document-calc-issue-title')[1]!.textContent).toContain('grid.calcError.cause.divideByZero');
  });

  it('проблем немає — панелі немає', () => {
    show(rowsOf({}));

    expect(screen.queryByTestId('document-calc-issues')).toBeNull();
  });
});
