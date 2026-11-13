import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { table } from '@/features/calculationIssues/__tests__/fixtures';
import type { FormulaErrorIssue } from '@/features/calculationIssues/api';
import { FormulaErrorsBanner } from '@/features/documents/FormulaErrorsBanner';
import { testTheme } from '@/test/render';

/**
 * ✎ C-1 (D-475): банер «Помилки формул» — група за таблицею, на комірку: причина (або джерело «не оновлено»),
 * формула, входи; «Перейти до комірки» відкриває САМЕ цю комірку (екземпляр, рядок, колонка). Розгорнутий за
 * замовчуванням — помилка блокує подання.
 *
 * ⚠ Каталог у тестах не завантажено — `t` дає позначений ключ `⟦ключ (параметри)⟧`.
 */

const Own: FormulaErrorIssue = {
  tableCode: 'T1',
  sheetCopyId: null,
  rowKey: 'R7',
  rowNumber: 7,
  columnCode: 'C',
  columnHeaderL10n: { values: { en: 'Ratio' } },
  kind: 'Error',
  errorCode: '#DIV/0',
  formula: '[A] / [B]',
  inputs: [
    { tableCode: 'T1', rowKey: 'R7', columnCode: 'A', periodOffset: 0, value: '10' },
    { tableCode: 'T1', rowKey: 'R7', columnCode: 'B', periodOffset: 0, value: '0' },
  ],
  source: null,
  since: '2026-11-11T10:00:00Z',
  rowLabelL10n: null,
  dependentCount: 1,
};

/** Залежні, чий корінь у переліку не показаний (інший період): один запис на джерело з кількістю. */
const Blocked: FormulaErrorIssue = {
  ...Own,
  columnCode: 'D',
  columnHeaderL10n: { values: { en: 'Doubled' } },
  rowLabelL10n: { values: { en: 'Boiler 1' } },
  kind: 'Blocked',
  formula: '[C] * 2',
  inputs: null,
  source: { tableCode: 'T1', rowKey: 'R7', columnCode: 'C', periodKey: 202512 },
  dependentCount: 3,
};

function show(errors: FormulaErrorIssue[], onOpenCell = vi.fn()) {
  render(
    <MantineProvider theme={testTheme}>
      <FormulaErrorsBanner userId={7} errors={errors} tables={[table(1, '9.01 Unit 400')]} onOpenCell={onOpenCell} />
    </MantineProvider>,
  );

  return onOpenCell;
}

describe('FormulaErrorsBanner (C-1)', () => {
  beforeEach(() => window.localStorage.clear());
  afterEach(() => window.localStorage.clear());

  it('група таблиці з причиною, формулою, входами і джерелом «не оновлено»', () => {
    show([Own, Blocked]);

    expect(screen.getAllByTestId('document-formula-errors-group')).toHaveLength(1);
    const [own, blocked] = screen.getAllByTestId('document-formula-error');
    expect(own!.getAttribute('data-kind')).toBe('Error');
    expect(own!.textContent).toContain('⟦grid.calcError.cause.divideByZero⟧');
    expect(own!.textContent).toContain('formula=[A] / [B]');
    expect(own!.textContent).toContain('inputs=A=10; B=0');
    // Корінь — один раз, залежні — кількістю, а не переліком.
    expect(own!.textContent).toContain('document.formulaErrors.dependents (count=1)');
    expect(own!.textContent).toContain('document.formulaErrors.cell (column=Ratio, row=7)');
    expect(blocked!.getAttribute('data-kind')).toBe('Blocked');
    expect(blocked!.textContent).toContain('document.formulaErrors.blockedBy (source=T1 · C (R7))');
    // Фіксований рядок — назвою шаблону; «не оновлено» — з кількістю комірок.
    expect(blocked!.textContent).toContain('document.formulaErrors.cellNamed (column=Doubled, row=Boiler 1)');
    expect(blocked!.textContent).toContain('count=3');
    expect(screen.getByTestId('document-formula-errors-summary').textContent).toContain('count=2');
  });

  it('«Перейти до комірки» відкриває саме цю комірку: екземпляр, рядок, колонка', async () => {
    const onOpenCell = show([Own, Blocked]);

    await userEvent.click(screen.getAllByTestId('document-formula-error-open')[1]!);

    expect(onOpenCell).toHaveBeenCalledWith(101, 'R7', 'D');
  });

  it('✎ B78: та сама помилка в кількох рядках — один рядок банера з номерами рядків і кількістю', async () => {
    // Land doc 96: «Gas density» з тим самим паливом (#N/A довідника) у рядках 1 і 2, інше паливо — у рядку 3.
    const density = (row: number, fuel: string): FormulaErrorIssue => ({
      ...Own,
      rowKey: `LTD-A0${String(row)}`,
      rowNumber: row,
      columnCode: 'Density',
      columnHeaderL10n: { values: { en: 'Gas density' } },
      errorCode: '#N/A',
      formula: 'LAND_HSE400_COMPOSITION([Land_TypeFuel])',
      inputs: [{ tableCode: 'T1', rowKey: `LTD-A0${String(row)}`, columnCode: 'Land_TypeFuel', periodOffset: 0, value: fuel }],
      dependentCount: 0,
      since: `2026-11-1${String(row)}T10:00:00Z`,
    });
    const onOpenCell = show([density(1, '37'), density(2, '37'), density(3, '36')]);

    const lines = screen.getAllByTestId('document-formula-error');
    expect(lines).toHaveLength(2);
    expect(lines[0]!.getAttribute('data-cells')).toBe('2');
    expect(lines[0]!.textContent).toContain('document.formulaErrors.cellRows (column=Gas density, rows=1, 2, count=2)');
    expect(lines[0]!.textContent).toContain('inputs=Land_TypeFuel=37');
    expect(lines[1]!.textContent).toContain('document.formulaErrors.cell (column=Gas density, row=3)');
    expect(lines[1]!.textContent).not.toContain('row=0');
    // Лічильник — комірки, а не рядки банера.
    expect(screen.getByTestId('document-formula-errors-summary').textContent).toContain('count=3');

    await userEvent.click(screen.getAllByTestId('document-formula-error-open')[0]!);
    expect(onOpenCell).toHaveBeenCalledWith(101, 'LTD-A01', 'Density');
  });

  it('без помилок банера немає', () => {
    show([]);

    expect(screen.queryByTestId('document-formula-errors')).toBeNull();
  });
});
