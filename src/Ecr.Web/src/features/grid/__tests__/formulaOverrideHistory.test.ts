import { describe, expect, it } from 'vitest';
import type { TableSliceDto } from '@/api/types';
import { annotateOverrideHistory, historyWriteOf, type OverrideHistoryLookups } from '@/features/grid/formulaOverride';
import { UndoStack, type CellEdit } from '@/features/grid/undo';

/**
 * ✎ D-461: Undo/Redo відтворюють СТАН комірки формули, а не лише число.
 * Undo заміни обчисленої комірки — повернення формули (`null`); Redo — те саме ручне значення з тією самою причиною;
 * Undo повернення формули — колишнє ручне значення з колишньою причиною.
 *
 * Колонка F (формула дозволяє ручне), r1–r3 обчислені, r4 — ручне «old reason»; A — колонка вводу.
 */
function slice(): TableSliceDto {
  return {
    tableInstanceId: 11,
    periodKey: 202610,
    columns: [],
    rows: [],
    cellPermissions: {},
    cellConfirmations: {},
    formulaOverride: {
      canOverride: true,
      overridableColumns: ['F'],
      overridableCells: [],
      manualCells: { 'r4:F': { reason: 'old reason', setBy: 'Admin', setAt: '2026-10-30T10:00:00Z' } },
    },
  } as unknown as TableSliceDto;
}

function lookups(
  reasons: Record<string, string>,
  held: Record<string, { value: unknown; isEmpty: boolean }> = {},
  previous: Record<string, string> = {},
): OverrideHistoryLookups {
  const key = (cell: { rowKey: string; columnCode: string }): string => `${cell.rowKey}:${cell.columnCode}`;

  return {
    held: (cell) => held[key(cell)],
    reason: (cell) => reasons[key(cell)],
    previousReason: (cell) => previous[key(cell)],
  };
}

/** Що піде на сервер для кроків, які повернула історія (як `applyHistory`). */
function writes(edits: readonly CellEdit[] | null): Record<string, { value: unknown; reason: string | null }> {
  return Object.fromEntries((edits ?? []).map((edit) => [`${edit.rowKey}:${edit.columnCode}`, historyWriteOf(edit)]));
}

describe('історія ручних значень замість формули', () => {
  it('одинична заміна: Undo повертає формулу, Redo — те саме значення з тією самою причиною', () => {
    const history = new UndoStack('t');
    history.push({
      label: 'edit',
      edits: annotateOverrideHistory(slice(), [{ rowKey: 'r1', columnCode: 'F', before: '2', after: '100' }], lookups({ 'r1:F': 'meter' })),
    });

    expect(writes(history.undo())).toEqual({ 'r1:F': { value: null, reason: null } });
    expect(writes(history.redo())).toEqual({ 'r1:F': { value: '100', reason: 'meter' } });
  });

  it('пакет (вставка/протягування): заміна, повернення формули й звичайна колонка — кожна у свій стан', () => {
    const history = new UndoStack('t');
    const edits: CellEdit[] = [
      { rowKey: 'r2', columnCode: 'F', before: '4', after: '40' },
      { rowKey: 'r3', columnCode: 'F', before: '6', after: '60' },
      { rowKey: 'r4', columnCode: 'F', before: '100', after: null },
      { rowKey: 'r1', columnCode: 'A', before: '1', after: '7' },
    ];
    const annotated = annotateOverrideHistory(slice(), edits, lookups({ 'r2:F': 'batch', 'r3:F': 'batch', 'r4:F': 'old reason' }));
    expect(annotated[3]).not.toHaveProperty('manualAfter');
    history.push({ label: 'paste', edits: annotated });

    expect(writes(history.undo())).toEqual({
      'r1:A': { value: '1', reason: null },
      // повернення формули скасовано — колишнє ручне з колишньою причиною
      'r4:F': { value: '100', reason: 'old reason' },
      // заміни скасовано — формула
      'r3:F': { value: null, reason: null },
      'r2:F': { value: null, reason: null },
    });
    expect(writes(history.redo())).toEqual({
      'r2:F': { value: '40', reason: 'batch' },
      'r3:F': { value: '60', reason: 'batch' },
      'r4:F': { value: null, reason: null },
      'r1:A': { value: '7', reason: null },
    });
  });

  it('нова заміна поверх ще не збереженої ручної: Undo повертає попереднє незбережене з його причиною', () => {
    // r1 вже має незбережене ручне 100 («first»), людина ставить 120 («second»).
    const annotated = annotateOverrideHistory(
      slice(),
      [{ rowKey: 'r1', columnCode: 'F', before: '100', after: '120' }],
      lookups({ 'r1:F': 'second' }, { 'r1:F': { value: '100', isEmpty: false } }, { 'r1:F': 'first' }),
    );
    expect(annotated[0]).toMatchObject({ manualBefore: { reason: 'first' }, manualAfter: { reason: 'second' } });

    const history = new UndoStack('t');
    history.push({ label: 'edit', edits: annotated });
    expect(writes(history.undo())).toEqual({ 'r1:F': { value: '100', reason: 'first' } });
  });

  it('крок «повернення до збереженого» бере стан «після» зі зрізу', () => {
    const [step] = annotateOverrideHistory(
      slice(),
      [{ rowKey: 'r1', columnCode: 'F', before: '100', after: '2' }],
      lookups({ 'r1:F': 'x' }, { 'r1:F': { value: '100', isEmpty: false } }),
      true,
    );
    expect(step).toMatchObject({ manualBefore: { reason: 'x' }, manualAfter: null });
  });

  it('без права — кроки без стану (звичайна історія)', () => {
    const noRight = { ...slice(), formulaOverride: { ...slice().formulaOverride!, canOverride: false } } as TableSliceDto;
    const [step] = annotateOverrideHistory(noRight, [{ rowKey: 'r1', columnCode: 'F', before: '2', after: '3' }], lookups({}));
    expect(step).not.toHaveProperty('manualBefore');
  });
});
