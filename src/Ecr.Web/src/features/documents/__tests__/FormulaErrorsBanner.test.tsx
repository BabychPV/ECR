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
};

const Blocked: FormulaErrorIssue = {
  ...Own,
  columnCode: 'D',
  columnHeaderL10n: { values: { en: 'Doubled' } },
  kind: 'Blocked',
  formula: '[C] * 2',
  inputs: null,
  source: { tableCode: 'T1', rowKey: 'R7', columnCode: 'C', periodKey: null },
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
    expect(blocked!.getAttribute('data-kind')).toBe('Blocked');
    expect(blocked!.textContent).toContain('document.formulaErrors.blockedBy (source=T1 · C (R7))');
    expect(screen.getByTestId('document-formula-errors-summary').textContent).toContain('count=2');
  });

  it('«Перейти до комірки» відкриває саме цю комірку: екземпляр, рядок, колонка', async () => {
    const onOpenCell = show([Own, Blocked]);

    await userEvent.click(screen.getAllByTestId('document-formula-error-open')[1]!);

    expect(onOpenCell).toHaveBeenCalledWith(101, 'R7', 'D');
  });

  it('без помилок банера немає', () => {
    show([]);

    expect(screen.queryByTestId('document-formula-errors')).toBeNull();
  });
});
