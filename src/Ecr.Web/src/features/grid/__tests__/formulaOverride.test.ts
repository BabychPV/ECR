import { describe, expect, it } from 'vitest';
import type { ColumnDto, PatchCellsRequest, TableSliceDto } from '@/api/types';
import {
  normalizeOverrideReason,
  overrideIntentOf,
  restorableCellsIn,
  splitOverrideEdits,
} from '@/features/grid/formulaOverride';
import { decide } from '@/features/grid/permissions';
import { cellStateOf } from '@/features/grid/cellState';
import { buildRequest } from '@/features/grid/cellPatchRequest';
import { markOverrideReason, resetOverrideReasons } from '@/features/grid/overrideReasons';
import { applyPatchToSlice } from '@/features/grid/sliceApply';

/**
 * ✎ D-461: ручне значення замість формули на сітці — правила клієнта над фактами зрізу (`formulaOverride`).
 * Сценарій людини: колонка вводу A, дві колонки формул F1 (дозволяє ручне) і F2 (не дозволяє), п'ять рядків;
 * r4 — ручне значення в F1, r5 — ручне в F2 (прапорець F2 вимкнули ПІСЛЯ того, як значення поставили).
 */
function column(code: string, dataType: string): ColumnDto {
  return { code, dataType, header: code, isReadOnly: false } as unknown as ColumnDto;
}

function slice(canOverride = true): TableSliceDto {
  return {
    tableInstanceId: 11,
    periodKey: 202610,
    columns: [column('A', 'Decimal'), column('F1', 'Formula'), column('F2', 'Formula')],
    rows: ['r1', 'r2', 'r3', 'r4', 'r5'].map((rowKey, ordinal) => ({
      rowKey,
      ordinal,
      rowKind: 'Data',
      rowVersion: 'v1',
      isOrphaned: false,
      label: null,
      cells: { A: '1', F1: '2', F2: '2' },
    })),
    cellPermissions: canOverride ? {} : { 'r1:F1': 'CalculatedCell', 'r4:F1': 'CalculatedCell' },
    cellConfirmations: {},
    formulaOverride: {
      canOverride,
      overridableColumns: ['F1'],
      overridableCells: [],
      manualCells: {
        'r4:F1': { reason: 'meter replaced', setBy: 'Admin', setAt: '2026-10-30T10:00:00Z' },
        'r5:F2': { reason: 'old', setBy: null, setAt: '2026-10-29T10:00:00Z' },
      },
    },
  } as unknown as TableSliceDto;
}

describe('overrideIntentOf', () => {
  it('нове значення в колонці, що дозволяє ручне, — причина; видалення — нічого або повернення', () => {
    const s = slice();
    expect(overrideIntentOf(s, 'r1', 'F1', '5')).toBe('reason');
    expect(overrideIntentOf(s, 'r1', 'F1', null)).toBe('noop');
    expect(overrideIntentOf(s, 'r4', 'F1', null)).toBe('restore');
    expect(overrideIntentOf(s, 'r4', 'F1', '7')).toBe('reason');
  });

  it('ручна комірка, формула якої вже не дозволяє, — лише повернення формули', () => {
    const s = slice();
    expect(overrideIntentOf(s, 'r5', 'F2', '3')).toBe('restoreOnly');
    expect(overrideIntentOf(s, 'r5', 'F2', '')).toBe('restore');
    expect(overrideIntentOf(s, 'r1', 'F2', '3')).toBe('none');
  });

  it('без права — звичайні правила (комірки формул не чіпаються)', () => {
    expect(overrideIntentOf(slice(false), 'r1', 'F1', '5')).toBe('none');
  });
});

describe('decide і стан комірки', () => {
  it('з правом: колонка, що дозволяє, і ручна комірка — редаговні; решта формул — CalculatedCell', () => {
    const s = slice();
    const [, f1, f2] = s.columns as [ColumnDto, ColumnDto, ColumnDto];
    expect(decide(s, 'r1', f1).editable).toBe(true);
    expect(decide(s, 'r5', f2).editable).toBe(true);
    expect(decide(s, 'r1', f2).reason).toBe('CalculatedCell');
  });

  it('без права: відмова сервера лишається', () => {
    const s = slice(false);
    expect(decide(s, 'r1', s.columns[1] as ColumnDto).reason).toBe('CalculatedCell');
  });

  it('комірку, яку можна замінити, досі показано обчисленою; ручну — ні', () => {
    const s = slice();
    const f1 = s.columns[1] as ColumnDto;
    expect(cellStateOf(s, 'r1', f1)).toBe('calculated');
    expect(cellStateOf(s, 'r4', f1)).toBeNull();
  });
});

describe('normalizeOverrideReason', () => {
  it('обрізає, зводить CRLF, тримає межу 1000 і відкидає керівні символи (крім переносу)', () => {
    expect(normalizeOverrideReason('  ')).toBeNull();
    expect(normalizeOverrideReason('  meter \r\n replaced ')).toBe('meter \n replaced');
    expect(normalizeOverrideReason('x'.repeat(1000))).toHaveLength(1000);
    expect(normalizeOverrideReason('x'.repeat(1001))).toBeNull();
    expect(normalizeOverrideReason('a\tb')).toBeNull();
  });
});

describe('splitOverrideEdits і restorableCellsIn', () => {
  it('пакет: нові ручні — з причиною, видалення формули — відкинуто, ручна з вимкненим прапорцем — відкинуто', () => {
    const edits = [
      { rowKey: 'r1', columnCode: 'A', value: '9', isEmpty: false },
      { rowKey: 'r2', columnCode: 'F1', value: '9', isEmpty: false },
      { rowKey: 'r3', columnCode: 'F1', value: null, isEmpty: false },
      { rowKey: 'r4', columnCode: 'F1', value: null, isEmpty: false },
      { rowKey: 'r5', columnCode: 'F2', value: '9', isEmpty: false },
    ];
    const split = splitOverrideEdits(slice(), edits);
    expect(split.kept.map((e) => `${e.rowKey}:${e.columnCode}`)).toEqual(['r1:A', 'r2:F1', 'r4:F1']);
    expect(split.reasonCells.map((e) => e.rowKey)).toEqual(['r2']);
    expect(split.noop.map((e) => e.rowKey)).toEqual(['r3']);
    expect(split.restoreOnly.map((e) => e.rowKey)).toEqual(['r5']);
  });

  it('«Повернути формулу» бачить ручні комірки виділення (з урахуванням колонки підписів)', () => {
    const range = { fromRow: 0, toRow: 4, fromColumn: 1, toColumn: 3 };
    expect(restorableCellsIn(slice(), range, 1)).toEqual([
      { rowKey: 'r4', columnCode: 'F1' },
      { rowKey: 'r5', columnCode: 'F2' },
    ]);
    expect(restorableCellsIn(slice(false), range, 1)).toEqual([]);
  });
});

describe('запит і локальне застосування', () => {
  it('buildRequest несе причину лише для значення; видалення (повернення формули) — без причини', () => {
    resetOverrideReasons();
    markOverrideReason(11, 202610, [{ rowKey: 'r2', columnCode: 'F1' }, { rowKey: 'r4', columnCode: 'F1' }], 'why');
    const request = buildRequest(11, 202610, [
      { rowKey: 'r2', columnCode: 'F1', value: '9', isEmpty: false, baseVersion: 'v1' },
      { rowKey: 'r4', columnCode: 'F1', value: null, isEmpty: false, baseVersion: 'v1' },
      { rowKey: 'r1', columnCode: 'A', value: '3', isEmpty: false, baseVersion: 'v1' },
    ]);
    const cells = request.rows.flatMap((row) => row.cells.map((cell) => ({ row: row.rowKey, ...cell })));
    expect(cells.find((c) => c.row === 'r2')?.overrideReason).toBe('why');
    expect(cells.find((c) => c.row === 'r4')).not.toHaveProperty('overrideReason');
    expect(cells.find((c) => c.row === 'r1')).not.toHaveProperty('overrideReason');
    resetOverrideReasons();
  });

  it('відповідь PATCH: нове ручне з’являється в manualCells, повернене — зникає', () => {
    const request = {
      tableInstanceId: 11,
      periodKey: 202610,
      origin: 'UserEdit',
      rows: [
        { rowKey: 'r2', baseVersion: 'v1', cells: [{ columnCode: 'F1', value: '9', isEmpty: false, overrideReason: 'why' }] },
        { rowKey: 'r4', baseVersion: 'v1', cells: [{ columnCode: 'F1', value: null, isEmpty: false }] },
      ],
    } as unknown as PatchCellsRequest;
    const next = applyPatchToSlice(
      slice(),
      request,
      { appliedCells: 2, rowVersions: {} } as never,
      'Admin',
      new Date('2026-10-30T12:00:00Z'),
    );
    expect(next.formulaOverride?.manualCells['r2:F1']).toEqual({
      reason: 'why',
      setBy: 'Admin',
      setAt: '2026-10-30T12:00:00.000Z',
    });
    expect(next.formulaOverride?.manualCells['r4:F1']).toBeUndefined();
    expect(next.formulaOverride?.manualCells['r5:F2']).toBeDefined();
  });
});
