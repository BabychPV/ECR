import { afterEach, describe, expect, it } from 'vitest';
import { act, cleanup, render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { GridFormulaBar } from '@/features/grid/GridFormulaBar';
import { publishFocus, resetFocus } from '@/features/grid/focusStore';
import { clearInspectedCell, inspectedCell } from '../inspectedCell';

/**
 * Рядок формули віддає комірку під курсором інспектору (`UI-25`): History
 * адресує журнал `columnDefId` (`ColumnDto.id`), а не кодом колонки.
 */
function column(overrides: Partial<ColumnDto>): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'Volume',
    dataType: 'Decimal',
    ordinal: 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: null,
    unitId: null,
    unitSymbol: 'm³',
    ...overrides,
  };
}

const Slice: TableSliceDto = {
  tableInstanceId: 7,
  periodKey: 202609,
  columns: [column({ id: 41, code: 'C1', scale: 4 }), column({ id: 42, code: 'F1', dataType: 'Formula', isReadOnly: true })],
  rows: [
    { rowKey: 'r1', ordinal: 1, rowKind: 'Item', label: 'Diesel', rowVersion: '0x01', cells: { C1: '5', F1: '12.5' }, isOrphaned: false },
  ],
  cellPermissions: {},
  cellConfirmations: {},
};

afterEach(() => {
  cleanup();
  act(() => {
    resetFocus();
    clearInspectedCell();
  });
});

describe('комірка для інспектора', () => {
  it('фокус у сітці публікує адресу й метадані комірки', () => {
    render(
      <MantineProvider>
        <GridFormulaBar
          tableInstanceId={7}
          periodKey={202609}
          slice={Slice}
          columns={[{ prop: 'C1' }, { prop: 'F1' }]}
          rows={[{ __rowKey: 'r1', __rowLabel: 'Diesel', C1: '5', F1: '12.5' }]}
          labelProp="__rowLabel"
        />
      </MantineProvider>,
    );

    expect(inspectedCell()).toBeNull();

    act(() => publishFocus(7, 202609, { rowIndex: 0, columnIndex: 1 }));

    expect(inspectedCell()).toMatchObject({
      tableInstanceId: 7,
      periodKey: 202609,
      rowKey: 'r1',
      rowLabel: 'Diesel',
      columnCode: 'F1',
      columnDefId: 42,
      isCalculated: true,
      isReadOnly: true,
      value: '12.5',
    });

    // Фокус пішов із сітки (клік в інспектор) — комірка лишається.
    act(() => publishFocus(7, 202609, null));
    expect(inspectedCell()?.columnDefId).toBe(42);
  });
});
