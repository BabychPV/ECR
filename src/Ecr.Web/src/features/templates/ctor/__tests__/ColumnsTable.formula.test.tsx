import { describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, within } from '@testing-library/react';
import { renderWithQuery } from '@/test/render';
import type { TemplateTable } from '../ctorModel';
import { ColumnsTable } from '../ColumnsTable';

/**
 * p3-1110: дія «Formula» на колонці конструктора — лише там, де сервер її прийме (`ColumnDef.IsComputed`: `Formula`
 * чи `Calculated`; інакше `ECR-TMPL-4227.formulaOnManualColumn` при збереженні), і на колонці з УЖЕ наявною
 * формулою — щоб її можна було прибрати.
 *
 * ⛔ Мутація (локально): показувати кнопку на кожній колонці (як було) — червоніє «ручна колонка без формули».
 */
function column(id: number, code: string, dataType: string, formulaExpression: string | null = null) {
  return {
    id,
    code,
    dataType,
    headerL10n: { values: { en: code } },
    isHidden: false,
    isReadOnly: false,
    unitSymbol: null,
    formulaExpression,
    formulaDialect: null,
    displayFormat: null,
  };
}

const table = {
  id: 41,
  code: 'T',
  nameL10n: { values: { en: 'T' } },
  ordinal: 1,
  rowMode: 'Fixed',
  maxDynamicRows: null,
  rows: [],
  columns: [
    column(1, 'MANUAL', 'Decimal'),
    column(2, 'FRM', 'Formula'),
    column(3, 'CALC', 'Calculated'),
    column(4, 'LEGACY', 'Decimal', '[FRM] * 2'),
  ],
} as unknown as TemplateTable;

function rowOf(code: string): HTMLElement {
  const cell = screen.getByText(`(${code})`);
  const row = cell.closest('tr');
  if (row === null) throw new Error(`немає рядка ${code}`);
  return row;
}

function formulaButton(code: string): HTMLElement | null {
  return within(rowOf(code)).queryByRole('button', { name: '⟦formulas.edit⟧' });
}

describe('ColumnsTable — «Formula» лише на обчислюваній колонці', () => {
  it('ручна колонка без формули дії не має; Formula/Calculated і колонка з наявною формулою — мають', () => {
    const onFormula = vi.fn();
    renderWithQuery(
      <ColumnsTable
        table={table}
        canEditPresentation={false}
        canEditStructure
        reorderPending={false}
        deletingCode={null}
        onReorder={vi.fn()}
        onUsage={vi.fn()}
        onPresentation={vi.fn()}
        onEdit={vi.fn()}
        onDelete={vi.fn()}
        onFormula={onFormula}
      />,
    );

    expect(formulaButton('MANUAL')).toBeNull();
    expect(formulaButton('FRM')).not.toBeNull();
    expect(formulaButton('CALC')).not.toBeNull();
    // Успадкована формула на ручній колонці: сервер дозволяє її прибрати — дія лишається.
    const legacy = formulaButton('LEGACY');
    expect(legacy).not.toBeNull();

    fireEvent.click(legacy as HTMLElement);
    expect(onFormula).toHaveBeenCalledWith(expect.objectContaining({ code: 'LEGACY' }));
  });
});
