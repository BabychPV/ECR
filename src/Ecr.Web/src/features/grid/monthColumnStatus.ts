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
): ReadonlyMap<string, MarkedMonthStatus> {
  const { data } = useQuery({
    queryKey: submissionTargetKey(documentId, sheetDefId ?? 0, periodKey),
    queryFn: () => fetchSubmissionTarget(documentId, sheetDefId ?? 0, periodKey),
    enabled: false,
  });

  // ⚠ Відповідь без переліку (порожнє тіло, старіший сервер) — колонки без позначок, а не падіння сітки.
  return useMemo(
    () => markedMonthColumns(Array.isArray(data?.monthColumns) ? data.monthColumns : undefined, tableDefId),
    [data, tableDefId],
  );
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
