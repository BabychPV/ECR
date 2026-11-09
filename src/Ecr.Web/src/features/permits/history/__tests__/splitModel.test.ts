import { describe, expect, it } from 'vitest';
import { EcrApiError } from '@/api/client';
import {
  addDecimals,
  dayCount,
  initialParts,
  isoOfDotted,
  permitGapOf,
  permitGridMarksOf,
  splitRefusalOf,
  totalOfParts,
} from '../splitModel';
import { historyOf, seaWaterRow } from './historyFixture';

/**
 * Feature (j) P3 model: which cells are split months, the exact total A + B (never pro-rata), part A pre-filled with
 * the old single value, and the two refusals (j3 splitRequired, j4 permitGap).
 */
function problem(errorCode: string, extensions2: Record<string, unknown>): EcrApiError {
  return new EcrApiError({ title: 't', status: 422, errorCode, correlationId: 'c', extensions2 });
}

describe('permitGridMarksOf', () => {
  it('marks the shared month cell of THIS table instance as needing a split, and keeps the row history', () => {
    const marks = permitGridMarksOf(historyOf(seaWaterRow()), 701, null);
    const feb = marks.cells.get('R1:Feb');

    expect(feb?.needsSplit).toBe(true);
    expect(feb?.isSplit).toBe(false);
    expect(marks.rows.get('R1')?.permits).toHaveLength(2);
  });

  it('another table or another sheet copy gets nothing', () => {
    expect(permitGridMarksOf(historyOf(seaWaterRow()), 702, null).cells.size).toBe(0);
    expect(permitGridMarksOf(historyOf(seaWaterRow()), 701, 9).cells.size).toBe(0);
  });

  it('a split cell is marked split, not needing a split', () => {
    const row = seaWaterRow();
    const month = row.splitMonths[0]!;
    const split = seaWaterRow({
      splitMonths: [{ ...month, columnCodes: [], cells: [{ columnCode: 'Feb', total: '12.4', parts: ['5.8', '6.6'], isSplit: true }] }],
    });
    const feb = permitGridMarksOf(historyOf(split), 701, null).cells.get('R1:Feb');

    expect(feb?.isSplit).toBe(true);
    expect(feb?.needsSplit).toBe(false);
  });
});

describe('parts and total', () => {
  it('part A starts with the old single value; part B empty (default 3)', () => {
    const feb = permitGridMarksOf(historyOf(seaWaterRow()), 701, null).cells.get('R1:Feb')!;

    expect(initialParts(feb)).toEqual(['12.4', null]);
  });

  it('total is exact decimal A + B, not float', () => {
    expect(addDecimals(['0.1', '0.2'])).toBe('0.3');
    expect(addDecimals(['5.8', '6.6'])).toBe('12.4');
    expect(addDecimals(['-1.25', '1'])).toBe('-0.25');
    expect(totalOfParts(['5.8', null])).toBeNull();
  });

  it('days of a part include both ends', () => {
    expect(dayCount('2026-02-01', '2026-02-14')).toBe(14);
    expect(dayCount('2026-02-15', '2026-02-28')).toBe(14);
  });
});

describe('refusals', () => {
  it('j3: ECR-CELL-4225 names the row and the cells to split', () => {
    const refusal = splitRefusalOf(
      problem('ECR-CELL-4225', { messageKey: 'err.ECR-CELL-4225.splitRequired', rowKey: 'R1', month: '02.2026', cells: [{ rowKey: 'R1', columnCode: 'Feb' }] }),
    );

    expect(refusal).toEqual({ rowKey: 'R1', month: '02.2026', cells: [{ rowKey: 'R1', columnCode: 'Feb' }] });
    expect(splitRefusalOf(problem('ECR-CELL-0422', { cells: [] }))).toBeNull();
  });

  it('j4: permitGap gives the start date that closes the gap as yyyy-MM-dd', () => {
    const gap = permitGapOf(
      problem('ECR-CELL-4223', { messageKey: 'err.ECR-CELL-4223.permitGap', next: 'KZ46', days: '10.02–14.02.2026', suggestedFrom: '10.02.2026' }),
    );

    expect(gap).toEqual({ next: 'KZ46', days: '10.02–14.02.2026', suggestedFrom: '2026-02-10' });
    expect(permitGapOf(problem('ECR-CELL-4223', { messageKey: 'err.ECR-CELL-4223.permitNotCoveringMonths' }))).toBeNull();
    expect(isoOfDotted('2026-02-10')).toBeNull();
  });
});
