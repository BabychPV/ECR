import { describe, expect, it } from 'vitest';
import type { PatchCellsRequest, PatchCellsResponse, TableSliceDto } from '@/api/types';
import { applyPatchToSlice } from '../sliceApply';

/**
 * AN-162: PATCH повертає `rowVersions` лише зачеплених рядків. Рядок, якого немає у відповіді, ЗБЕРІГАЄ свою версію
 * (мапа зливається, а не замінюється) - інакше наступна правка того рядка пішла б без `baseVersion`.
 */
function row(rowKey: string, rowVersion: string): TableSliceDto['rows'][number] {
  return { rowKey, ordinal: 1, rowKind: 'Static', label: null, rowVersion, cells: { C1: 1 }, isOrphaned: false } as TableSliceDto['rows'][number];
}

const slice = {
  tableInstanceId: 700,
  periodKey: 202609,
  columns: [],
  rows: [row('R1', 'v1'), row('R2', 'v1'), row('R3', 'v1')],
  cellPermissions: {},
  cellConfirmations: {},
} as unknown as TableSliceDto;

describe('applyPatchToSlice - часткова мапа rowVersions (AN-162)', () => {
  it('оновлює версію зачепленого рядка, решта рядків лишається зі своєю версією', () => {
    const request: PatchCellsRequest = {
      origin: 'UserEdit',
      tableInstanceId: 700,
      periodKey: 202609,
      rows: [{ rowKey: 'R2', baseVersion: 'v1', cells: [{ columnCode: 'C1', isEmpty: false, value: '7' }] }],
    };
    const response: PatchCellsResponse = { appliedCells: 1, rowVersions: { R2: 'v2' }, validation: [] };

    const next = applyPatchToSlice(slice, request, response);

    expect(next.rows.map((r) => r.rowVersion)).toEqual(['v1', 'v2', 'v1']);
  });
});
