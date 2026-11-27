import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { issue, table } from '@/features/calculationIssues/__tests__/fixtures';
import type { FormulaErrorIssue } from '@/features/calculationIssues/api';
import type { MonthUnmatchedRows, UnmatchedRows } from '@/features/documents/useUnmatchedRows';
import { testTheme } from '@/test/render';
import { CalcProblemsLine, calcProblemTotal } from '../CalcProblemsLine';

/**
 * ✎ DOCUX (огляд UI 27.11): Issues казав «Not validated yet», а рядок повідомлень — «Calculation finished with problems».
 * Тепер Issues називає їх («Calculation problems: N — see Status»); N — сума причин смуги без відсіяних фільтром активності.
 */
function formulaError(row: number): FormulaErrorIssue {
  return {
    tableCode: 'T1', sheetCopyId: null, rowKey: `F-${String(row)}`, rowNumber: row, columnCode: 'Total',
    columnHeaderL10n: { values: { en: 'Total' } }, kind: 'Error', errorCode: '#DIV/0', formula: '[A] / [B]', inputs: [],
    source: null, since: '2099-11-13T10:00:00Z', rowLabelL10n: null, dependentCount: 0,
  };
}

const rowsOf = (over: Partial<UnmatchedRows>): UnmatchedRows => ({ issues: [], hints: [], finishedAt: '2099-11-13T11:41:00Z', formulaErrors: [], ...over });

// Та сама розкладка, що в `CalculationIssuesStrip.test`: 2 помилки формул · 4 рядки без входів · 3 неактивні.
const Months: readonly MonthUnmatchedRows[] = [
  { periodKey: 209909, rows: rowsOf({ formulaErrors: [formulaError(1), formulaError(2)], issues: [issue(1, 3, { kind: 'MissingInputs' })] }) },
  { periodKey: 209910, rows: rowsOf({ issues: [issue(1, 4, { kind: 'MissingInputs' }), issue(1, 5, { kind: 'MissingInputs' }), issue(1, 6, { kind: 'RowInactive', affectedCount: 3 })] }) },
  { periodKey: 209911, rows: rowsOf({ issues: [issue(1, 7, { kind: 'MissingInputs' })] }) },
];
const Tables = [table(1, 'Stationary')];

describe('CalcProblemsLine', () => {
  it('N — без неактивних; посилання відкриває Status', () => {
    expect(calcProblemTotal(Months, Tables)).toBe(6);
    const open = vi.fn();
    render(
      <MantineProvider theme={testTheme}>
        <CalcProblemsLine months={Months} tables={Tables} onOpen={open} />
      </MantineProvider>,
    );
    expect(screen.getByText('⟦inspector.calcProblems (count=6)⟧')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '⟦inspector.calcProblemsOpen⟧' }));
    expect(open).toHaveBeenCalledTimes(1);
  });

  it('проблем немає (або лише неактивні) — рядка немає', () => {
    const onlyInactive: MonthUnmatchedRows[] = [{ periodKey: 209910, rows: rowsOf({ issues: [issue(1, 6, { kind: 'RowInactive', affectedCount: 3 })] }) }];
    const { container } = render(
      <MantineProvider theme={testTheme}>
        <CalcProblemsLine months={onlyInactive} tables={Tables} onOpen={() => undefined} />
        <CalcProblemsLine months={undefined} tables={Tables} onOpen={() => undefined} />
      </MantineProvider>,
    );
    expect(container.querySelector('[data-inspector-calc-problems]')).toBeNull();
  });
});
