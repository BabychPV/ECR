import { afterEach, describe, expect, it } from 'vitest';
import { act, render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { clearInspectedCell, inspectedCell } from '@/features/documents/inspector/inspectedCell';
import { GridFormulaBar } from '../GridFormulaBar';
import { publishFocus, resetFocus } from '../focusStore';
import { formulaBarModel } from '../formulaBar';
import { atLineStart } from '../rowKeyText';

/**
 * ✎ DOCUX (живий прохід 27.11): довідникова комірка показувала «Generator»/«Diesel», а рядок формули — «Value: 7», інспектор —
 * «Stored value 37» (id запису). Тепер рядок формули й інспектор — ТОЙ САМИЙ показ, що комірка (`displayOf` = правило
 * `lookupCellDisplay` сітки), id — лише поруч в інспекторі. І рядок на початку адреси — з великої літери («Row 18»).
 */
function column(overrides: Partial<ColumnDto>): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'Type of fuel',
    dataType: 'Lookup',
    ordinal: 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: 5,
    unitId: null,
    unitSymbol: null,
    ...overrides,
  };
}

const Slice: TableSliceDto = {
  tableInstanceId: 7,
  periodKey: 202606,
  columns: [column({ id: 1, code: 'FUEL' }), column({ id: 2, code: 'QTY', header: 'Quantity', dataType: 'Decimal', lookupRegistryDefId: null })],
  rows: [
    // Динамічний рядок без підпису, згенерований ключ — не код для людини.
    { rowKey: 'p1-T3_00-0018', ordinal: 18, rowKind: 'Dynamic', label: null, rowVersion: '0x01', cells: { FUEL: 37, QTY: '5' }, isOrphaned: false },
  ],
  cellPermissions: {},
  cellConfirmations: {},
};

const GridColumns = [{ prop: 'FUEL' }, { prop: 'QTY' }];
const GridRows = [{ __rowKey: 'p1-T3_00-0018', __rowLabel: 'p1-T3_00-0018', FUEL: 37, QTY: '5' }];
const Labels = new Map([[37, 'Diesel']]);
const displayOf = (columnCode: string, value: unknown): string | null =>
  columnCode === 'FUEL' ? (Labels.get(Number(value)) ?? String(value)) : null;

afterEach(() => {
  resetFocus();
  clearInspectedCell();
});

describe('рядок формули: довідникова комірка й адреса', () => {
  it('модель: показ комірки поруч із записом; збігається із записом чи правила немає — null', () => {
    const at = (columnIndex: number, show?: typeof displayOf) =>
      formulaBarModel({
        slice: Slice,
        columns: GridColumns,
        rows: GridRows,
        labelProp: '__rowLabel',
        focus: { rowIndex: 0, columnIndex },
        ...(show === undefined ? {} : { displayOf: show }),
      });

    expect(at(0, displayOf)).toMatchObject({ value: '37', valueLabel: 'Diesel' });
    expect(at(0)).toMatchObject({ value: '37', valueLabel: null });
    expect(at(1, displayOf)).toMatchObject({ value: '5', valueLabel: null });
    // Запису ще немає в переліку (довідник вантажиться) — комірка теж показує id: показ дорівнює запису → null.
    expect(at(0, () => '37')).toMatchObject({ valueLabel: null });
  });

  it('екран: «Value: Diesel», «Row 18 · Type of fuel»; інспектор отримує і запис, і показ', () => {
    render(
      <MantineProvider>
        <GridFormulaBar tableInstanceId={7} periodKey={202606} slice={Slice} columns={GridColumns} rows={GridRows} labelProp="__rowLabel" displayOf={displayOf} />
      </MantineProvider>,
    );

    act(() => {
      publishFocus(7, 202606, { rowIndex: 0, columnIndex: 0 });
    });

    const value = document.querySelector('[data-formula-bar-value]')?.textContent ?? '';
    expect(value).toContain('value=Diesel');
    expect(value).not.toContain('value=37');
    // `permits.history.rowPosition` («row {n}») — на початку адреси з великої літери; ключ рядка не показується.
    const address = document.querySelector('[data-formula-bar-address]')?.textContent ?? '';
    expect(address).toContain('row=⟦permits.history.rowPosition (n=1)⟧');
    expect(address).not.toContain('p1-T3_00-0018');

    expect(inspectedCell()).toMatchObject({ rowKey: 'p1-T3_00-0018', value: '37', valueLabel: 'Diesel' });
  });

  it('підпис рядка на початку адреси — з великої літери (ключ каталогу «row {n}» лишається малим для середини речення)', () => {
    expect(atLineStart('row 18')).toBe('Row 18');
    expect(atLineStart('строка 18')).toBe('Строка 18');
    expect(atLineStart('18-жол')).toBe('18-жол');
    expect(atLineStart('')).toBe('');
  });
});
