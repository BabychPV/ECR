import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { fetchSubmissionTarget, submissionTargetKey, type MonthColumnStatus } from '@/features/workflow/api';

/*
 * ⚠ Окремий модуль, а не `features/workflow/api.ts`: читає його лише чанк сітки, а `api.ts` сидить у чанку
 * маршруту `DocumentPage` (бюджет `D-132`, 250 КБ gzip).
 */

/** Стан місячної колонки, який позначається в заголовку (D-441): подано чи затверджено. */
export type MarkedMonthStatus = 'Submitted' | 'Approved';

/**
 * Статуси місячних колонок ОДНІЄЇ таблиці: код колонки → `Submitted`/`Approved` (D-441).
 *
 * ⚠ Лише ЧИТАЄ відповідь, яку вже запитав рядок дій (`useSheetActions`): `enabled: false`, тож сітка не
 * робить власного запиту на кожну таблицю, а оновлюється разом із кешем після дії робочого процесу.
 * Чернетка й відхилене не позначаються — колонка без позначки і є «ще не подано».
 */
export function useMonthColumnStatuses(
  documentId: number,
  sheetDefId: number | undefined,
  periodKey: number,
  tableDefId: number,
  sheetCopyId: number | null = null,
): ReadonlyMap<string, MarkedMonthStatus> {
  const { data } = useQuery({
    queryKey: submissionTargetKey(documentId, sheetDefId ?? 0, periodKey, sheetCopyId),
    queryFn: () => fetchSubmissionTarget(documentId, sheetDefId ?? 0, periodKey, sheetCopyId),
    enabled: false,
  });

  // ⚠ Відповідь без переліку (порожнє тіло, старіший сервер) — колонки без позначок, а не падіння сітки.
  return useMemo(
    () => markedMonthColumns(Array.isArray(data?.monthColumns) ? data.monthColumns : undefined, tableDefId),
    [data, tableDefId],
  );
}

/** Місяць колонки: номер 1..12 і період, якому вона відповідає. */
export interface MonthOfColumn {
  readonly monthNumber: number;
  readonly periodKey: number;
}

/**
 * ✎ 12.11 (людина): місяць кожної місячної колонки ОДНІЄЇ таблиці — щоб Issues називав закриті місяці назвами
 * («January, April and July»), а не заголовками колонок. Та сама відповідь, що й у `useMonthColumnStatuses`
 * (`enabled: false`, нового запиту немає); відповіді ще немає — порожньо (колонка без місяця = місяць зрізу).
 */
export function useMonthColumnMonths(
  documentId: number,
  sheetDefId: number | undefined,
  periodKey: number,
  tableDefId: number,
  sheetCopyId: number | null = null,
): ReadonlyMap<string, MonthOfColumn> {
  const { data } = useQuery({
    queryKey: submissionTargetKey(documentId, sheetDefId ?? 0, periodKey, sheetCopyId),
    queryFn: () => fetchSubmissionTarget(documentId, sheetDefId ?? 0, periodKey, sheetCopyId),
    enabled: false,
  });

  return useMemo(
    () => monthColumnsOf(Array.isArray(data?.monthColumns) ? data.monthColumns : undefined, tableDefId),
    [data, tableDefId],
  );
}

/** Код колонки → місяць для однієї таблиці. */
export function monthColumnsOf(
  months: readonly MonthColumnStatus[] | undefined,
  tableDefId: number,
): ReadonlyMap<string, MonthOfColumn> {
  const result = new Map<string, MonthOfColumn>();
  for (const month of months ?? []) {
    if (month.tableDefId === tableDefId) result.set(month.columnCode, { monthNumber: month.monthNumber, periodKey: month.periodKey });
  }

  return result;
}

/** Код колонки → `Submitted`/`Approved` для однієї таблиці. */
export function markedMonthColumns(
  months: readonly MonthColumnStatus[] | undefined,
  tableDefId: number,
): ReadonlyMap<string, MarkedMonthStatus> {
  const marked = new Map<string, MarkedMonthStatus>();
  for (const month of months ?? []) {
    if (month.tableDefId !== tableDefId) continue;
    if (month.sheetStatus === 'Submitted' || month.sheetStatus === 'Approved') marked.set(month.columnCode, month.sheetStatus);
  }

  return marked;
}
