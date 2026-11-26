import { QueryClient } from '@tanstack/react-query';
import { describe, expect, it } from 'vitest';
import { calculationIssuesKey, calculationIssuesYearKey } from '../api';
import { refreshStaleness, settleRecalculation } from '@/features/grid/useCellPatch';

/**
 * ✎ DOCUX L1 (BUG-6, AC L1-4): виправили комірку рядка з переліку проблем розрахунку — перелік (смуга над сіткою)
 * перечитується одразу, без фокусу вікна; перерахунок формул документа теж скидає переліки. Без фіксу червоні:
 * `refreshStaleness` інвалідував лише картку й результати, `settleRecalculation` — лише зрізи.
 */
const listed = { items: [{ rowKey: 'R-1' }], formulaErrors: [{ rowKey: 'R-9' }], tables: [] };

function client(): QueryClient {
  const qc = new QueryClient();
  qc.setQueryData(calculationIssuesKey(7, 202609, null), listed);
  qc.setQueryData(calculationIssuesYearKey(7, 202601, null), { items: [], formulaErrors: [{ rowKey: 'R-9' }], tables: [] });
  qc.setQueryData(calculationIssuesKey(8, 202609, null), listed);
  return qc;
}

const invalidated = (qc: QueryClient, key: readonly unknown[]): boolean => qc.getQueryState(key)?.isInvalidated === true;

describe('переліки проблем розрахунку після збереження й перерахунку (DOCUX L1)', () => {
  it('збережено рядок із переліку — перелік цього документа інвалідується; чужий документ — ні', () => {
    const qc = client();
    refreshStaleness(qc, 7, 202609, ['R-1']);

    expect(invalidated(qc, calculationIssuesKey(7, 202609, null))).toBe(true);
    expect(invalidated(qc, calculationIssuesYearKey(7, 202601, null))).toBe(false);
    expect(invalidated(qc, calculationIssuesKey(8, 202609, null))).toBe(false);
  });

  it('рядок помилки формули (рік) — інвалідується перелік року', () => {
    const qc = client();
    refreshStaleness(qc, 7, 202609, ['R-9']);

    expect(invalidated(qc, calculationIssuesYearKey(7, 202601, null))).toBe(true);
  });

  it('збережено рядок, якого в переліку немає — запиту немає (автозбереження не платить GET)', () => {
    const qc = client();
    refreshStaleness(qc, 7, 202609, ['R-404']);

    expect(invalidated(qc, calculationIssuesKey(7, 202609, null))).toBe(false);
    expect(invalidated(qc, calculationIssuesYearKey(7, 202601, null))).toBe(false);
  });

  it('перерахунок формул документа завершився — усі переліки документа інвалідуються', () => {
    const qc = client();
    settleRecalculation(qc, `job-docux-${String(Date.now())}`, 7, 202609, 3, [202609]);

    expect(invalidated(qc, calculationIssuesKey(7, 202609, null))).toBe(true);
    expect(invalidated(qc, calculationIssuesYearKey(7, 202601, null))).toBe(true);
    expect(invalidated(qc, calculationIssuesKey(8, 202609, null))).toBe(false);
  });
});
