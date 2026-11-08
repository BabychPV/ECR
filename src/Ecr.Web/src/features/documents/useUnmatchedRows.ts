import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { CalculationRunStatusDto } from '@/api/types';
import { apiFetch } from '@/api/client';
import type { CalculationIssue, CalculationIssueTableHint } from '@/features/calculationIssues/api';
import { useCalculationIssues } from '@/features/calculationIssues/useCalculationIssues';
import { calculationResultsKey } from '@/features/methodologies/calculationResultsKey';

/** Рядки документа без правила розрахунку і час прогону, що їх знайшов. */
export interface UnmatchedRows {
  /** Рядки оригіналів аркушів і (якщо відкрита копія) рядки цієї копії. */
  readonly issues: readonly CalculationIssue[];
  /** Що приймають правила таблиць із проблемами (UAT 08.11 b3); порожньо — підказок немає. */
  readonly hints: readonly CalculationIssueTableHint[];
  /** Кінець актуального прогону (UTC); `null` — невідомо (немає права `Calculation.View` чи прогону). */
  readonly finishedAt: string | null;
}

const Empty: readonly CalculationIssue[] = [];
const NoHints: readonly CalculationIssueTableHint[] = [];

/**
 * Рядки документа, яким не підійшло жодне правило розрахунку (D14 + AN-69, UAT 08.11 b1).
 *
 * ⛔ Перелік — `GET …/calculation-issues` (усі рядки, з кодом таблиці), а не профіль прогону
 * (`GET …/calculation-run`): профіль зберігає лише перші 10 ключів без назви таблиці, і банер на 60 рядків
 * показував «1 (S01), 2 (S02), 1 (S01)…» — однакові ключі різних таблиць. Профіль лишається джерелом ЧАСУ прогону.
 *
 * ⚠ Запит проблем — на оригінали (`sheetCopyId = null`) і на відкриту копію: той самий ключ кешу, що й позначки
 * в дереві таблиць (`SheetWorkspace`), тож другого запиту для оригіналу немає.
 *
 * ⚠ Ключ часу прогону — під `calculationResultsKey(...)`: завершений перерахунок інвалідує його за префіксом.
 * Без права `Calculation.View` сервер відповів би 403 — тоді час просто не показується.
 */
export function useUnmatchedRows(
  documentId: number,
  periodKey: number,
  activeCopyId: number | null,
  canViewRun: boolean,
): UnmatchedRows {
  const originals = useCalculationIssues(documentId, periodKey, null);
  const copy = useCalculationIssues(documentId, periodKey, activeCopyId);
  const run = useQuery({
    queryKey: [...calculationResultsKey(documentId, periodKey), 'run'],
    queryFn: () =>
      apiFetch<CalculationRunStatusDto>(
        `/api/v1/documents/${String(documentId)}/calculation-run?periodKey=${String(periodKey)}`,
      ),
    enabled: canViewRun,
    retry: false,
  });

  const issues = useMemo(() => {
    const own = (originals.data?.items ?? Empty).filter((issue) => (issue.sheetCopyId ?? null) === null);
    if (activeCopyId === null) return own;

    return [...own, ...(copy.data?.items ?? Empty).filter((issue) => issue.sheetCopyId === activeCopyId)];
  }, [originals.data, copy.data, activeCopyId]);

  const hints = useMemo(() => {
    const own = (originals.data?.tables ?? NoHints).filter((hint) => (hint.sheetCopyId ?? null) === null);
    if (activeCopyId === null) return own;

    return [...own, ...(copy.data?.tables ?? NoHints).filter((hint) => hint.sheetCopyId === activeCopyId)];
  }, [originals.data, copy.data, activeCopyId]);

  return { issues, hints, finishedAt: run.data?.finishedAt ?? null };
}
