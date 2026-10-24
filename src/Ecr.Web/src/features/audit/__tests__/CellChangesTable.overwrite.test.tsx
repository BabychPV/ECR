import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { CellChangesTable, showsOverwriteMark, type CellChange } from '@/features/audit/CellChangesTable';
import { testTheme } from '@/test/render';

/**
 * ✎ D-447: «перезаписав чужу правку» — окремий прапорець журналу (`isOverwrite`). Позначка стоїть поруч із
 * походженням, коли воно інше (правка погоджувача); звичайний перезапис і так читається з `ImportOverwrite`,
 * тож другої такої самої позначки в рядку немає.
 */

const Base: CellChange = {
  changedAt: '2026-10-24T10:00:00Z',
  periodKey: 202610,
  documentId: 1,
  rowKey: 'R1',
  columnDefId: 2,
  oldValue: '1',
  newValue: '2',
  changedByUserId: 3,
  origin: 'ApproverEdit',
  isLateEdit: false,
  isOutOfWindow: false,
  fromArchive: false,
  isOverwrite: false,
  changedByDisplayName: 'D. Nurlanova',
  documentBusinessKey: 'AKT-001',
  documentNameL10n: null,
  columnCode: 'CO2',
  columnHeaderL10n: { values: { en: 'CO2 emissions' } },
  columnDataType: 'Decimal',
};

function rows(items: CellChange[]): HTMLElement[] {
  render(
    <MantineProvider theme={testTheme}>
      <CellChangesTable items={items} />
    </MantineProvider>,
  );

  return screen.getAllByRole('row').slice(1) as HTMLElement[];
}

describe('CellChangesTable: позначка «перезаписав чужу правку» (D-447)', () => {
  it('правка погоджувача, що перезаписала чужу, має обидві позначки; без перезапису — лише походження', () => {
    const [overwritten, plain] = rows([
      { ...Base, rowKey: 'A', isOverwrite: true },
      { ...Base, rowKey: 'B', isOverwrite: false },
    ]);

    // Мутація «прибрати OriginCell / гілку isOverwrite» лишає перший рядок без позначки.
    const mark = (overwritten as HTMLElement).querySelector('[data-audit-overwrite]') as HTMLElement;
    expect(mark).not.toBeNull();
    expect(mark.textContent).toBe('⟦audit.overwriteMark⟧');
    expect(mark.getAttribute('title')).toBe('⟦audit.overwriteHint⟧');
    expect((overwritten as HTMLElement).querySelector('[data-audit-origin="ApproverEdit"]')).not.toBeNull();

    expect((plain as HTMLElement).querySelector('[data-audit-overwrite]')).toBeNull();
  });

  it('звичайний перезапис (ImportOverwrite) не дублює позначку', () => {
    const [row] = rows([{ ...Base, origin: 'ImportOverwrite', isOverwrite: true }]);

    // Мутація «прибрати умову origin !== ImportOverwrite» дає тут другу позначку.
    expect((row as HTMLElement).querySelector('[data-audit-overwrite]')).toBeNull();
    expect((row as HTMLElement).querySelector('[data-audit-origin="ImportOverwrite"]')).not.toBeNull();
  });

  it('showsOverwriteMark: лише прапорець і походження, відмінне від ImportOverwrite', () => {
    expect(showsOverwriteMark({ isOverwrite: true, origin: 'ApproverEdit' })).toBe(true);
    expect(showsOverwriteMark({ isOverwrite: true, origin: 'ImportOverwrite' })).toBe(false);
    expect(showsOverwriteMark({ isOverwrite: false, origin: 'ApproverEdit' })).toBe(false);
  });
});
