import { afterEach, describe, expect, it } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { GridFormulaBar } from '../GridFormulaBar';
import { publishFocus, resetFocus } from '../focusStore';

/**
 * Рядок формули на екрані (`UI-08`).
 *
 * ⛔ Фокус приходить зі СХОВИЩА (`focusStore.ts`), а не пропом: саме це й
 * перевіряється — компонент оновлюється від публікації фокуса, без жодного
 * перемальовування сітки навколо.
 *
 * ⚠ Каталог у тесті не завантажений (той самий випадок, що й у
 * `gridColumns.requiredIndicator.test.ts`): `t()` повертає позначений ключ, і
 * твердження дивляться на ключ. Людський текст несе `09-seed.sql`.
 */

function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'Викиди',
    dataType: 'Decimal',
    ordinal: 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: null,
    unitId: null,
    unitSymbol: null,
    ...overrides,
  };
}

const Slice: TableSliceDto = {
  tableInstanceId: 7,
  periodKey: 202609,
  columns: [column({ id: 1, code: 'C1' }), column({ id: 2, code: 'F1', dataType: 'Formula' })],
  rows: [
    {
      rowKey: 'r1',
      ordinal: 1,
      rowKind: 'Item',
      label: 'Діоксид вуглецю',
      rowVersion: '0x01',
      cells: { C1: '5', F1: '12.5' },
      isOrphaned: false,
    },
  ],
  cellPermissions: {},
  cellConfirmations: {},
};

const GridColumns = [{ prop: 'C1' }, { prop: 'F1' }];
const GridRows = [{ __rowKey: 'r1', __rowLabel: 'Діоксид вуглецю', C1: '5', F1: '12.5' }];

function show(expressions?: ReadonlyMap<string, string>): void {
  render(
    <MantineProvider>
      <GridFormulaBar
        tableInstanceId={7}
        periodKey={202609}
        slice={Slice}
        columns={GridColumns}
        rows={GridRows}
        labelProp="__rowLabel"
        {...(expressions === undefined ? {} : { expressionByColumnCode: expressions })}
      />
    </MantineProvider>,
  );
}

function bar(): HTMLElement {
  const found = document.querySelector('[data-formula-bar]');
  if (found === null) throw new Error('Рядка формули немає в DOM');

  return found as HTMLElement;
}

afterEach(() => {
  // ⛔ Сховище модульне й переживає кінець тесту — так само, як
  // `pendingStore.resetPending`: фокус одного сценарію інакше потрапив би в
  // наступний.
  resetFocus();
});

describe('GridFormulaBar', () => {
  it('без фокуса — підказка «оберіть комірку», а не порожнє місце', () => {
    show();

    expect(bar().dataset['formulaBar']).toBe('empty');
    expect(bar().textContent).toContain('formulaBarEmpty');
  });

  it('публікація фокуса оновлює рядок без перемальовування сітки', () => {
    show();

    act(() => {
      publishFocus(7, 202609, { rowIndex: 0, columnIndex: 0 });
    });

    expect(bar().dataset['formulaBar']).toBe('cell');
    expect(bar().textContent).toContain('Діоксид вуглецю');
  });

  it('фокус ЧУЖОГО зрізу цей рядок не чіпає', () => {
    /*
     * ⛔ На аркуші одночасно змонтовано кілька сіток (`SheetTables.tsx`).
     * Мутація, яку це ловить: спільний фокус на весь модуль — тоді клік в
     * одній таблиці посував би рядок формули в усіх.
     */
    show();

    act(() => {
      publishFocus(999, 202609, { rowIndex: 0, columnIndex: 0 });
    });

    expect(bar().dataset['formulaBar']).toBe('empty');
  });

  it('обчислювана комірка з виразом показує САМ ВИРАЗ', () => {
    show(new Map([['F1', 'SUM(C1:C10)']]));

    act(() => {
      publishFocus(7, 202609, { rowIndex: 0, columnIndex: 1 });
    });

    /*
     * ⛔ Мутаційний доказ вимоги «рядок формули»: прибери гілку `expression`
     * із `GridFormulaBar` — і цього вузла в DOM не буде, тест падає
     * `Рядка формули немає` / `expected null not to be null`.
     */
    const expression = bar().querySelector('[data-formula-bar-expression]');
    expect(expression?.textContent).toBe('SUM(C1:C10)');
    expect(bar().querySelector('[data-formula-bar-calculated]')).not.toBeNull();
  });

  it('обчислювана комірка без виразу КАЖЕ про це, а не мовчить', () => {
    // ⚠ Порожнє місце тут читалося б як «формули немає»; сервер виразу поки
    // не віддає взагалі (`formulaBar.ts`), і це названо словами.
    show();

    act(() => {
      publishFocus(7, 202609, { rowIndex: 0, columnIndex: 1 });
    });

    expect(bar().querySelector('[data-formula-bar-expression]')).toBeNull();
    expect(bar().querySelector('[data-formula-bar-no-expression]')?.textContent).toContain(
      'formulaBarNoExpression',
    );
  });

  it('звичайна комірка не оголошується обчислюваною', () => {
    show(new Map([['F1', 'SUM(C1:C10)']]));

    act(() => {
      publishFocus(7, 202609, { rowIndex: 0, columnIndex: 0 });
    });

    expect(bar().querySelector('[data-formula-bar-calculated]')).toBeNull();
    expect(bar().querySelector('[data-formula-bar-expression]')).toBeNull();
    expect(bar().querySelector('[data-formula-bar-value]')?.textContent).toContain('value=5');
  });

  it('рядок формули має доступне ім\'я — читалка має назвати, що це за смуга', () => {
    show();

    expect(screen.getByRole('group', { name: /formulaBarLabel/ })).toBeTruthy();
  });
});
