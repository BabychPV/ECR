import { describe, expect, it } from 'vitest';
import type { CalculationIssues } from '@/features/calculationIssues/api';
import { formulaErrorsTruncated } from '@/features/documents/calculationIssuesPanelModel';
import { splitIssuesByMonth } from '@/features/documents/useUnmatchedRows';

/**
 * ✎ W15z M4: відповідь `calculation-issues?year=true` документа «рік» — по місяцях (`monthNumber`): лише місяці з
 * проблемами, за зростанням; копія — лише відкрита; джерело — лише з оригіналу; час прогону — лише якорю.
 */
const Anchor = 209901;

const issue = (monthNumber: number | null, sheetCopyId: number | null = null) => ({
  tableCode: 'T0', sheetCopyId, rowNumber: 1, rowKey: `R${String(monthNumber)}`, kind: 'StepError', methodologyCode: 'M1',
  detectedAt: '2099-11-03T08:00:00Z', monthNumber,
});

const formulaError = (monthNumber: number) =>
  ({
    tableCode: 'T0', sheetCopyId: null, rowKey: 'R1', rowOrdinal: 1, rowLabelL10n: null, columnCode: 'M01',
    columnHeaderL10n: null, kind: 'Error', errorCode: '#VALUE', formula: null, inputs: null, source: null,
    since: '2099-11-03T08:00:00Z', dependentCount: 0, monthNumber,
  }) as unknown as NonNullable<CalculationIssues['formulaErrors']>[number];

const source = (monthNumber: number) => ({
  kind: 'SourceChangedAfterApproval', monthNumber, sourcePeriodKey: 209900 + monthNumber, sourceDocumentId: 7,
  sourceProjectCode: 'HSE', sourceDocumentKey: 'K', sourceApprovedAt: null, approvedAt: '2099-11-02T08:00:00Z', sourceChangedAt: null,
});

describe('splitIssuesByMonth', () => {
  it('розкладає рядки, помилки формул і джерело по місяцях; чисті місяці пропущено; час — лише якорю', () => {
    const originals = {
      items: [issue(10), issue(3), issue(10)],
      formulaErrors: [formulaError(1)],
      sourceIssues: [source(10)],
    } as unknown as CalculationIssues;

    const months = splitIssuesByMonth(Anchor, originals, originals, null, '2099-11-04T08:00:00Z');

    expect(months.map((m) => m.periodKey)).toEqual([209901, 209903, 209910]);
    expect(months[0]!.rows.formulaErrors).toHaveLength(1);
    expect(months[0]!.rows.finishedAt).toBe('2099-11-04T08:00:00Z');
    expect(months[1]!.rows.issues).toHaveLength(1);
    expect(months[1]!.rows.finishedAt).toBeNull();
    expect(months[2]!.rows.issues).toHaveLength(2);
    expect(months[2]!.rows.sourceIssues).toHaveLength(1);
  });

  // ✎ Огляд M4 (2): сервер віддає ≤ 500 записів року ПО ПОРЯДКУ місяців, а `formulaErrorTotal` — усі комірки року. Обрізано —
  // місяць зрізу (останній із записами) і пізніші несуть «N+»; раніші — повні, без позначки.
  it('стеля записів року: місяць зрізу позначено «обрізано» (N+), ранніший — ні; без обрізання — ніхто', () => {
    const errors = [formulaError(1), formulaError(3)];
    const truncated = splitIssuesByMonth(Anchor, { items: [issue(5)], formulaErrors: errors, formulaErrorTotal: 7 } as unknown as CalculationIssues, undefined, null, null);
    const byMonth = new Map(truncated.map((m) => [m.periodKey, m.rows]));
    expect(formulaErrorsTruncated(byMonth.get(209901)!)).toBe(false);
    expect(formulaErrorsTruncated(byMonth.get(209903)!)).toBe(true);
    expect(formulaErrorsTruncated(byMonth.get(209905)!)).toBe(true);

    const whole = splitIssuesByMonth(Anchor, { items: [], formulaErrors: errors, formulaErrorTotal: 2 } as unknown as CalculationIssues, undefined, null, null);
    expect(whole.map((m) => formulaErrorsTruncated(m.rows))).toEqual([false, false]);
  });

  it('копія: лише рядки відкритої копії; рядок без monthNumber — до якоря; без відповіді — порожньо', () => {
    const originals = { items: [issue(null), issue(5, 42)] } as unknown as CalculationIssues;
    const copy = { items: [issue(5, 42), issue(6, 43)] } as unknown as CalculationIssues;

    const months = splitIssuesByMonth(Anchor, originals, copy, 42, null);

    expect(months.map((m) => [m.periodKey, m.rows.issues.length])).toEqual([[209901, 1], [209905, 1]]);
    expect(splitIssuesByMonth(Anchor, undefined, undefined, null, null)).toEqual([]);
  });
});
