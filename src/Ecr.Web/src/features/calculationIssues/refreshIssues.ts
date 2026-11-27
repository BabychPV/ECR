import type { QueryClient, QueryKey } from '@tanstack/react-query';
import type { CalculationIssues } from './api';

/**
 * ✎ DOCUX L1 (BUG-6): смуга «Calculation: rows not included…» не оновлювалась після виправлення комірки — запит
 * `calculation-issues` після збереження ніхто не інвалідував (лише картку й результати), і він чекав фокусу вікна.
 *
 * Ключі проблем — `['document', id, період, 'calculation-issues' | 'calculation-issues-year', копія]` (`api.ts`).
 */
function isCalcIssuesKey(key: QueryKey, documentId: number): boolean {
  return (
    key[0] === 'document' &&
    key[1] === documentId &&
    (key[3] === 'calculation-issues' || key[3] === 'calculation-issues-year')
  );
}

/**
 * Після збереження рядків: перечитати ті переліки проблем документа, де є хоч один збережений рядок. Інші збереження
 * (рядки, яких у переліку немає) запиту не дають — автозбереження не платить зайвим `GET` за кожну правку.
 */
export function refreshCalcIssuesAfterSave(queryClient: QueryClient, documentId: number, rowKeys: readonly string[]): void {
  if (rowKeys.length === 0) return;
  const saved = new Set(rowKeys);
  const cached = queryClient.getQueriesData<CalculationIssues>({
    predicate: (query) => isCalcIssuesKey(query.queryKey, documentId),
  });

  for (const [key, data] of cached) {
    if (data === undefined) continue;
    const listed = [...(data.items ?? []), ...(data.formulaErrors ?? [])].some((issue) => saved.has(issue.rowKey));
    if (listed) void queryClient.invalidateQueries({ queryKey: key, exact: true });
  }
}

/** Після перерахунку формул документа (сітка): усі переліки проблем документа застаріли. */
export function refreshCalcIssues(queryClient: QueryClient, documentId: number): void {
  void queryClient.invalidateQueries({ predicate: (query) => isCalcIssuesKey(query.queryKey, documentId) });
}
