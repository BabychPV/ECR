import type { DocumentTableDto } from '@/api/types';
import type { CalculationIssue, CalculationIssues } from './api';

/**
 * Проблеми, що стосуються таблиць активного аркуша.
 *
 * Запит повертає проблеми всіх таблиць документа для копії (або для оригіналів), а екран показує один аркуш,
 * тому зайве відсікається: за копією аркуша (`sheetCopyId` збігається) і за кодом таблиці серед таблиць аркуша.
 * Порядок - як у відповіді сервера.
 */
export function issuesOfSheet(
  data: CalculationIssues | undefined,
  tables: readonly DocumentTableDto[],
  sheetCopyId: number | null,
): readonly CalculationIssue[] {
  // `items` обов'язковий у контракті; перевірка лишається, бо відповідь без нього не має валити сторінку.
  const items: readonly CalculationIssue[] | undefined = data?.items;
  if (!Array.isArray(items)) return [];

  const codes = new Set(tables.map((table) => table.tableCode));

  return items.filter((issue) => (issue.sheetCopyId ?? null) === sheetCopyId && codes.has(issue.tableCode));
}

/** Скільки проблемних рядків у кожній таблиці (за кодом); таблиці без проблем у мапі немає. */
export function countByTable(issues: readonly CalculationIssue[]): ReadonlyMap<string, number> {
  const counts = new Map<string, number>();
  for (const issue of issues) counts.set(issue.tableCode, (counts.get(issue.tableCode) ?? 0) + 1);

  return counts;
}
