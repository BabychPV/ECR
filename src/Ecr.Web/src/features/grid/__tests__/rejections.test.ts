import { afterEach, describe, expect, it } from 'vitest';
import { EcrApiError } from '@/api/client';
import type { PendingEdit } from '../useCellPatch';
import {
  markPendingRejected,
  openDocument,
  pendingRejections,
  pendingSlices,
  putPendingEdit,
  resetPending,
  sendableEdits,
} from '../pendingStore';
import { rejectionMarksOf } from '../saveErrors';

/**
 * `V-01` на рівні модулів: які відмови ТРИМАЮТЬ правки і як утримання
 * знімається. Повний сценарій «відхилена + нова правка» — у
 * `DocumentGrid.rejectedCellBlocking.test.tsx`.
 */

const Table = 1;
const Period = 202609;

function edit(rowKey: string, columnCode: string, value: unknown): PendingEdit {
  return { rowKey, columnCode, value, isEmpty: false, baseVersion: 'v1' };
}

function problem(status: number, errorCode: string, extensions2?: Record<string, unknown>): EcrApiError {
  return new EcrApiError({
    title: 't',
    status,
    errorCode,
    correlationId: 'c',
    detail: 'причина',
    ...(extensions2 === undefined ? {} : { extensions2 }),
  });
}

afterEach(() => resetPending());

describe('rejectionMarksOf — які відмови тримають правки', () => {
  const bad = edit('r1', 'C1', 'abc');
  const good = edit('r1', 'C2', 7);

  it('422 з названою колонкою тримає рівно її комірки', () => {
    const marks = rejectionMarksOf(problem(422, 'ECR-CELL-0422', { columnCode: 'C1' }), [bad, good]);

    expect(marks.map((mark) => mark.edit)).toEqual([bad]);
    expect(marks[0]?.scope).toBe('cell');
  });

  it('422 без названої комірки тримає ВЕСЬ пакет — інакше він упав би знову цілим', () => {
    expect(rejectionMarksOf(problem(422, 'ECR-X-0422'), [bad, good])).toHaveLength(2);
  });

  it('5xx і мережа НЕ тримають: повтор має везти ті самі правки', () => {
    expect(rejectionMarksOf(problem(500, 'ECR-SYS-0500'), [bad])).toEqual([]);
    expect(rejectionMarksOf(problem(429, 'ECR-REQ-0429'), [bad])).toEqual([]);
    expect(rejectionMarksOf(new TypeError('Failed to fetch'), [bad])).toEqual([]);
  });

  it('409 тримає розбіжні комірки з переліку — і лише їх', () => {
    const marks = rejectionMarksOf(
      problem(409, 'ECR-CELL-0409', { conflicts: [{ rowKey: 'r1', columnCode: 'C2', theirValue: 1 }] }),
      [bad, good],
    );

    expect(marks.map((mark) => mark.edit)).toEqual([good]);
    expect(marks[0]?.scope).toBe('cell');
  });

  it('409 без переліку тримає весь пакет — інакше він пішов би знову й знову', () => {
    expect(rejectionMarksOf(problem(409, 'ECR-CELL-0409'), [bad, good])).toHaveLength(2);
  });

  it('ECR-CALC-0437 тримає правки названих РЯДКІВ із рівнем «рядок»', () => {
    const other = edit('r2', 'C1', 5);
    const marks = rejectionMarksOf(
      problem(422, 'ECR-CALC-0437', { cells: [{ rowKey: 'r1', columnCode: 'C9', ruleCode: 'x', message: 'm' }] }),
      [bad, good, other],
    );

    expect(marks.map((mark) => mark.edit)).toEqual([bad, good]);
    expect(marks.every((mark) => mark.scope === 'row')).toBe(true);
  });
});

describe('сховище: утримання відхилених правок', () => {
  it('автозбереження не бачить утриманої правки, але вона лишається незбереженою', () => {
    openDocument(1);
    putPendingEdit(Table, Period, edit('r1', 'C1', 'abc'));
    putPendingEdit(Table, Period, edit('r1', 'C2', 7));

    markPendingRejected(Table, Period, [{ edit: edit('r1', 'C1', 'abc'), message: 'm', scope: 'cell' }]);

    expect(sendableEdits(Table, Period).map((e) => e.columnCode)).toEqual(['C2']);
    expect(pendingSlices({ sendableOnly: true })[0]?.edits.map((e) => e.columnCode)).toEqual(['C2']);
    expect(pendingSlices()[0]?.edits).toHaveLength(2);
  });

  it('позначка не ставиться на значення, якого сервер не відхиляв', () => {
    openDocument(1);
    putPendingEdit(Table, Period, edit('r1', 'C1', 42));

    // Доки запит із `abc` летів, оператор уже виправив комірку.
    expect(markPendingRejected(Table, Period, [{ edit: edit('r1', 'C1', 'abc'), message: 'm', scope: 'cell' }])).toBe(0);
    expect(pendingRejections(Table, Period).size).toBe(0);
  });

  it('відмову рівня рядка відпускає будь-яка нова правка того самого рядка', () => {
    openDocument(1);
    putPendingEdit(Table, Period, edit('r1', 'C1', 5));
    markPendingRejected(Table, Period, [{ edit: edit('r1', 'C1', 5), message: 'm', scope: 'row' }]);
    expect(sendableEdits(Table, Period)).toHaveLength(0);

    putPendingEdit(Table, Period, edit('r1', 'C9', 1));

    expect(sendableEdits(Table, Period).map((e) => e.columnCode).sort()).toEqual(['C1', 'C9']);
  });
});
