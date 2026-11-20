import { afterEach, describe, expect, it } from 'vitest';
import { act, cleanup, render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { GridFormulaBar } from '@/features/grid/GridFormulaBar';
import { publishFocus, resetFocus } from '@/features/grid/focusStore';
import { monthColumnsOf, type MonthOfColumn } from '@/features/grid/monthColumnStatus';
import { clearInspectedCell, inspectedCell } from '@/features/documents/inspector/inspectedCell';

/**
 * ✎ LAND-YEAR L7b (п. 3 плану): комірка місячної колонки публікується з `ownerPeriodKey` — період її місяця (`monthNumber`
 * і `periodKey` колонки з `monthColumns` відповіді `submission-target`), а не періодом перегляду (якорем року). Не-місячна
 * колонка і сітка без місяців (копія на місяць без маршрутизації) — `null`.
 *
 * ⛔ Мутаційний доказ (READY): `ownerPeriodKey: null` у `GridFormulaBar` — червоний перший тест.
 */
function column(overrides: Partial<ColumnDto>): ColumnDto {
  return {
    id: 1, code: 'C1', header: 'Volume', dataType: 'Decimal', ordinal: 1, isReadOnly: false, isRequired: false,
    isRequiredByMethodology: false, displayFormat: null, defaultValue: null, lookupRegistryDefId: null, unitId: null,
    unitSymbol: null, ...overrides,
  };
}

const Anchor = 209901;

const Slice: TableSliceDto = {
  tableInstanceId: 7,
  periodKey: Anchor,
  columns: [column({ id: 41, code: 'NAME', dataType: 'String' }), column({ id: 42, code: 'M10', header: 'October' })],
  rows: [
    { rowKey: 'r1', ordinal: 1, rowKind: 'Item', label: 'Gas', rowVersion: '0x01', cells: { NAME: 'x', M10: '5' }, isOrphaned: false },
  ],
  cellPermissions: {},
  cellConfirmations: {},
};

// Та сама форма, що дає `useMonthColumnMonths`: лише колонки цієї таблиці (tableDefId 5).
const Months: ReadonlyMap<string, MonthOfColumn> = monthColumnsOf(
  [
    { tableDefId: 5, columnCode: 'M10', monthNumber: 10, periodKey: 209910, sheetStatus: 'Draft', canDecide: false, canRecall: false, canReturn: false },
    { tableDefId: 6, columnCode: 'NAME', monthNumber: 3, periodKey: 209903, sheetStatus: 'Draft', canDecide: false, canRecall: false, canReturn: false },
  ] as Parameters<typeof monthColumnsOf>[0],
  5,
);

function mount(columnMonths: ReadonlyMap<string, MonthOfColumn> | undefined): void {
  render(
    <MantineProvider>
      <GridFormulaBar
        tableInstanceId={7}
        periodKey={Anchor}
        slice={Slice}
        columns={[{ prop: 'NAME' }, { prop: 'M10' }]}
        rows={[{ __rowKey: 'r1', __rowLabel: 'Gas', NAME: 'x', M10: '5' }]}
        labelProp="__rowLabel"
        {...(columnMonths === undefined ? {} : { columnMonths })}
      />
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  act(() => {
    resetFocus();
    clearInspectedCell();
  });
});

describe('GridFormulaBar: період-власник комірки', () => {
  it('місячна колонка M10 — власник жовтень (209910), період перегляду лишається якорем', () => {
    mount(Months);
    act(() => publishFocus(7, Anchor, { rowIndex: 0, columnIndex: 1 }));

    expect(inspectedCell()).toMatchObject({ columnCode: 'M10', periodKey: Anchor, ownerPeriodKey: 209910 });
  });

  it('не-місячна колонка — null (місяць іншої таблиці не підхоплюється)', () => {
    mount(Months);
    act(() => publishFocus(7, Anchor, { rowIndex: 0, columnIndex: 0 }));

    expect(inspectedCell()).toMatchObject({ columnCode: 'NAME', ownerPeriodKey: null });
  });

  it('сітка без місяців колонок — null, як доти', () => {
    mount(undefined);
    act(() => publishFocus(7, Anchor, { rowIndex: 0, columnIndex: 1 }));

    expect(inspectedCell()).toMatchObject({ columnCode: 'M10', ownerPeriodKey: null });
  });
});
