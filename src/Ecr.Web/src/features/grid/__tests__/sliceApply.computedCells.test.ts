import { describe, expect, it } from 'vitest';
import type { PatchCellsRequest, PatchCellsResponse, TableSliceDto } from '@/api/types';
import { applyPatchToSlice } from '../sliceApply';

/** HSE401: `computedCells` відповіді PATCH одразу лягають у зріз (Компонент/Фаза за кодом забруднювача). */
const slice = {
  tableInstanceId: 7,
  periodKey: 202601,
  columns: [],
  rows: [
    { rowKey: 'R1', ordinal: 1, rowKind: 'Dynamic', label: null, rowVersion: 'v1', cells: { Code: null, Component: 'old' }, isOrphaned: false },
    { rowKey: 'R2', ordinal: 2, rowKind: 'Dynamic', label: null, rowVersion: 'v1', cells: { Code: 5, Component: 'keep' }, isOrphaned: false },
  ],
  cellPermissions: {},
  cellConfirmations: {},
} as unknown as TableSliceDto;

const request: PatchCellsRequest = {
  origin: 'UserEdit',
  tableInstanceId: 7,
  periodKey: 202601,
  rows: [{ rowKey: 'R1', baseVersion: 'v1', cells: [{ columnCode: 'Code', isEmpty: false, value: '9' }] }],
};

describe('applyPatchToSlice - computedCells', () => {
  it('обчислені комірки відредагованого рядка з відповіді лягають у зріз; чужий рядок не чіпається', () => {
    const response: PatchCellsResponse = {
      appliedCells: 1,
      rowVersions: { R1: 'v2' },
      validation: [],
      computedCells: [
        { rowKey: 'R1', columnCode: 'Component', value: 'CO2' },
        { rowKey: 'R1', columnCode: 'Phase', value: 'gas' },
        { rowKey: 'R2', columnCode: 'Component', value: 'IGNORED' },
      ],
    };

    const next = applyPatchToSlice(slice, request, response);

    expect(next.rows[0]?.cells).toEqual({ Code: '9', Component: 'CO2', Phase: 'gas' });
    expect(next.rows[1]).toBe(slice.rows[1]);
  });

  it('null у перегляді очищає комірку; без computedCells зріз такий, як був', () => {
    const cleared = applyPatchToSlice(slice, request, {
      appliedCells: 1,
      rowVersions: {},
      validation: [],
      computedCells: [{ rowKey: 'R1', columnCode: 'Component', value: null }],
    });
    expect(cleared.rows[0]?.cells['Component']).toBeNull();

    const plain = applyPatchToSlice(slice, request, { appliedCells: 1, rowVersions: {}, validation: [] });
    expect(plain.rows[0]?.cells['Component']).toBe('old');
  });
});