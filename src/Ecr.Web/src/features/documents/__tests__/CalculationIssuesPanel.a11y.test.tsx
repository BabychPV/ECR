import { describe, expect, it } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { issue, table } from '@/features/calculationIssues/__tests__/fixtures';
import type { FormulaErrorIssue } from '@/features/calculationIssues/api';
import { CalculationIssuesPanel } from '@/features/documents/CalculationIssuesPanel';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';

/**
 * ✎ B78: панель «Проблеми розрахунку» в обох темах — заголовок, рядки причин з бейджами й кнопками дій, перемикач
 * «Технічні деталі» з `aria-expanded` і розгорнуті вкладені переліки — без блокуючих порушень axe (контраст бейджів
 * `statusError`/`brand` на світлому й темному тлі).
 */
function error(row: number, fuel: string): FormulaErrorIssue {
  return {
    tableCode: 'T1',
    sheetCopyId: null,
    rowKey: `LTD-A0${String(row)}`,
    rowNumber: row,
    columnCode: 'Density',
    columnHeaderL10n: { values: { en: 'Gas density' } },
    kind: 'Error',
    errorCode: '#N/A',
    formula: "REGFIELD(REGFIND('LAND_HSE400_COMPOSITION', [Land_TypeFuel]), 'VALUE')",
    inputs: [{ tableCode: 'T1', rowKey: `LTD-A0${String(row)}`, columnCode: 'Land_TypeFuel', periodOffset: 0, value: fuel }],
    source: null,
    since: '2026-11-13T10:00:00Z',
    rowLabelL10n: null,
    dependentCount: 1,
  };
}

describe('CalculationIssuesPanel — доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: причини, дії і розгорнуті технічні деталі без порушень`, async () => {
      render(
        <Shell colorScheme={colorScheme}>
          <CalculationIssuesPanel
            userId={undefined}
            periodKey={202610}
            rows={{
              issues: [issue(1, 2), issue(2, 1)],
              hints: [],
              finishedAt: '2026-11-13T11:41:00Z',
              formulaErrors: [error(1, 'Diesel'), error(2, 'Gasoline'), { ...error(3, '0'), formula: '[A] / [B]', errorCode: '#DIV/0' }],
            }}
            tables={[table(1, '3.00 Stationary equipment'), table(2, '9.03 Unit 430')]}
            onOpenCell={() => {}}
            onOpenHse400={() => Promise.resolve(true)}
          />
        </Shell>,
      );
      fireEvent.click(screen.getByTestId('document-calc-issues-details-toggle'));

      const violations = await findViolations(document.body);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });
  }
});
