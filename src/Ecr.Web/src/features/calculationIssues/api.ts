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

/** ✎ B80 N4: місяць джерела (HSE400) зв'язки `ApprovedOnly`, який не затверджено або змінено після затвердження. */
export type SourceIssue = components['schemas']['SourceIssueDto'];

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
 * ✎ W15z M4: ключ проблем ВСІХ місяців року документа «рік» (`year=true`) — під тим самим префіксом
 * `['document', id, якір]`, тож перерахунок на завершенні скидає й його.
 */
export const calculationIssuesYearKey = (documentId: number, anchorPeriodKey: number, sheetCopyId: number | null) =>
  ['document', documentId, anchorPeriodKey, 'calculation-issues-year', sheetCopyId] as const;

/**
 * `GET /documents/{id}/calculation-issues` - проблеми розрахунку за період; `sheetCopyId` лише для копії
 * (без нього сервер віддає оригінал аркуша). Право `Document.View`; невидимий документ - 404.
 *
 * ✎ W15z M4: `year` — документ «рік» (LAND-YEAR L6): проблеми всіх місяців року періоду, кожна з `monthNumber`.
 *
 * Адреса записана поруч із викликом (так її шукає сторож споживачів дій сервера); рядок запиту збирається
 * окремо, щоб шлях не закінчувався інтерполяцією.
 */
export function getCalculationIssues(
  documentId: number,
  periodKey: number,
  sheetCopyId: number | null,
  year = false,
): Promise<CalculationIssues> {
  const query = new URLSearchParams({ periodKey: String(periodKey) });
  if (sheetCopyId !== null) query.set('sheetCopyId', String(sheetCopyId));
  if (year) query.set('year', 'true');

  return apiFetch<CalculationIssues>(`/api/v1/documents/${String(documentId)}/calculation-issues?${query.toString()}`);
}
