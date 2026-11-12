import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/** Один рядок, який правило методології не охопило (AN-69). Типи - із `schema.d.ts`. */
export type CalculationIssue = components['schemas']['CalculationIssueDto'];

/** Поточні проблеми розрахунку документа за період і копію аркуша. */
export type CalculationIssues = components['schemas']['CalculationIssuesDto'];

/** Що приймають правила методологій таблиці з проблемами (UAT 08.11 b3). */
export type CalculationIssueTableHint = components['schemas']['CalculationIssueTableHintDto'];

/** ✎ C-1 (D-475): рядок групи «Помилки формул» (комірка, формула, причина, входи, джерело). */
export type FormulaErrorIssue = components['schemas']['FormulaErrorIssueDto'];

/**
 * Ключ кешу проблем розрахунку.
 *
 * Ключ лежить ПІД префіксом `['document', id, period]`: перерахунок на завершенні вже скидає цей префікс
 * (`SheetActions`, `refreshSheetWorkflow`), тож список оновлюється без окремої проводки. Копія аркуша - частина
 * ключа: оригінал і кожна копія мають власний перелік (`null` - оригінал).
 */
export const calculationIssuesKey = (documentId: number, periodKey: number, sheetCopyId: number | null) =>
  ['document', documentId, periodKey, 'calculation-issues', sheetCopyId] as const;

/**
 * `GET /documents/{id}/calculation-issues` - проблеми розрахунку за період; `sheetCopyId` лише для копії
 * (без нього сервер віддає оригінал аркуша). Право `Document.View`; невидимий документ - 404.
 *
 * Адреса записана поруч із викликом (так її шукає сторож споживачів дій сервера); рядок запиту збирається
 * окремо, щоб шлях не закінчувався інтерполяцією.
 */
export function getCalculationIssues(
  documentId: number,
  periodKey: number,
  sheetCopyId: number | null,
): Promise<CalculationIssues> {
  const query = new URLSearchParams({ periodKey: String(periodKey) });
  if (sheetCopyId !== null) query.set('sheetCopyId', String(sheetCopyId));

  return apiFetch<CalculationIssues>(`/api/v1/documents/${String(documentId)}/calculation-issues?${query.toString()}`);
}
