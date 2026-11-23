import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { CalculationRunStatusDto } from '@/api/types';
import { apiFetch } from '@/api/client';
import type {
  CalculationIssue,
  CalculationIssueTableHint,
  FormulaErrorIssue,
  SourceIssue,
} from '@/features/calculationIssues/api';
import type { CalculationIssues } from '@/features/calculationIssues/api';
import { useCalculationIssues, useYearCalculationIssues } from '@/features/calculationIssues/useCalculationIssues';
import { calculationResultsKey } from '@/features/methodologies/calculationResultsKey';

/** Рядки документа без правила розрахунку і час прогону, що їх знайшов. */
export interface UnmatchedRows {
  /** Рядки оригіналів аркушів і (якщо відкрита копія) рядки цієї копії. */
  readonly issues: readonly CalculationIssue[];
  /** Що приймають правила таблиць із проблемами (UAT 08.11 b3); порожньо — підказок немає. */
  readonly hints: readonly CalculationIssueTableHint[];
  /** Кінець актуального прогону (UTC); `null` — невідомо (немає права `Calculation.View` чи прогону). */
  readonly finishedAt: string | null;
  /** ✎ C-1 (D-475): група «Помилки формул» — оригінали й (якщо відкрита) копія; порожньо — помилок немає. */
  readonly formulaErrors: readonly FormulaErrorIssue[];
  /** ✎ C-1: скільки комірок з помилкою всього (сервер віддає не більше 500 рядків групи); без — довжина переліку. */
  readonly formulaErrorTotal?: number;
  /** ✎ B80 N4: місяці джерела HSE400, не затверджені чи змінені після затвердження (документ, не копія); без — немає. */
  readonly sourceIssues?: readonly SourceIssue[];
}

const Empty: readonly CalculationIssue[] = [];
const NoHints: readonly CalculationIssueTableHint[] = [];
const NoErrors: readonly FormulaErrorIssue[] = [];
const NoSourceIssues: readonly SourceIssue[] = [];

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

  const formulaErrors = useMemo(() => {
    const own = (originals.data?.formulaErrors ?? NoErrors).filter((error) => (error.sheetCopyId ?? null) === null);
    if (activeCopyId === null) return own;

    return [
      ...own,
      ...(copy.data?.formulaErrors ?? NoErrors).filter((error) => error.sheetCopyId === activeCopyId),
    ];
  }, [originals.data, copy.data, activeCopyId]);

  const formulaErrorTotal =
    (originals.data?.formulaErrorTotal ?? 0) + (activeCopyId === null ? 0 : (copy.data?.formulaErrorTotal ?? 0));

  return {
    issues,
    hints,
    finishedAt: run.data?.finishedAt ?? null,
    formulaErrors,
    formulaErrorTotal: Math.max(formulaErrorTotal, formulaErrors.length),
    // ✎ B80 N4: проблеми джерела — рівня документа-місяця, тож лише з запиту оригіналу (копія дала б ті самі).
    sourceIssues: originals.data?.sourceIssues ?? NoSourceIssues,
  };
}

/** ✎ W15z M4: проблеми розрахунку одного місяця документа «рік». */
export interface MonthUnmatchedRows {
  /** Місяць `yyyyMM`. */
  readonly periodKey: number;
  readonly rows: UnmatchedRows;
}

/** Чи є в місяці що показати на панелі. */
export function hasCalculationIssues(rows: UnmatchedRows): boolean {
  return rows.formulaErrors.length > 0 || rows.issues.length > 0 || (rows.sourceIssues?.length ?? 0) > 0;
}

/**
 * ✎ W15z M4: розкласти відповідь `year=true` (кожен рядок — з `monthNumber` місяця прогону) по місяцях року якоря; лише
 * місяці з проблемами, за зростанням. Рядок без `monthNumber` (не мав би бути в «рік») — до якоря. Час прогону відомий
 * лише якорю (`calculation-run` якоря) — інші місяці без нього, щоб не показувати чужий час.
 */
export function splitIssuesByMonth(
  anchorPeriodKey: number,
  originals: CalculationIssues | undefined,
  copy: CalculationIssues | undefined,
  activeCopyId: number | null,
  anchorFinishedAt: string | null,
): MonthUnmatchedRows[] {
  const year = Math.floor(anchorPeriodKey / 100);
  const monthOf = (monthNumber: number | null | undefined): number =>
    monthNumber === null || monthNumber === undefined ? anchorPeriodKey : year * 100 + monthNumber;
  const own = <T extends { readonly sheetCopyId?: number | null }>(list: readonly T[] | null | undefined, copyList: readonly T[] | null | undefined): T[] => [
    ...(list ?? []).filter((item) => (item.sheetCopyId ?? null) === null),
    ...(activeCopyId === null ? [] : (copyList ?? []).filter((item) => item.sheetCopyId === activeCopyId)),
  ];

  const months = new Map<number, { issues: CalculationIssue[]; formulaErrors: FormulaErrorIssue[]; sourceIssues: SourceIssue[] }>();
  const bucket = (periodKey: number) => {
    let found = months.get(periodKey);
    if (found === undefined) {
      found = { issues: [], formulaErrors: [], sourceIssues: [] };
      months.set(periodKey, found);
    }
    return found;
  };
  for (const issue of own(originals?.items, copy?.items)) bucket(monthOf(issue.monthNumber)).issues.push(issue);
  for (const error of own(originals?.formulaErrors, copy?.formulaErrors)) bucket(monthOf(error.monthNumber)).formulaErrors.push(error);
  // ✎ B80 N4: проблеми джерела — рівня документа-місяця, лише з запиту оригіналу.
  for (const source of originals?.sourceIssues ?? []) bucket(monthOf(source.monthNumber)).sourceIssues.push(source);

  const hints = own(originals?.tables, copy?.tables);

  return [...months.entries()]
    .sort(([a], [b]) => a - b)
    .map(([periodKey, month]) => ({
      periodKey,
      rows: {
        issues: month.issues,
        hints,
        finishedAt: periodKey === anchorPeriodKey ? anchorFinishedAt : null,
        formulaErrors: month.formulaErrors,
        formulaErrorTotal: month.formulaErrors.length,
        sourceIssues: month.sourceIssues,
      },
    }))
    .filter((month) => hasCalculationIssues(month.rows));
}

/**
 * ✎ W15z M4 (P-1122-4: «так познач щоб користувач бачив це»): документ «рік» відкривається на ЯКОРІ року (L7a), а
 * проблеми розрахунку (F5-R «джерело змінилось після затвердження», StepError, помилки формул) живуть у місяці прогону.
 * Доти сторінка читала лише якір — причина жовтня не показувалась ніде. Тепер — один запит `year=true` (сервер L6) і
 * перелік місяців із проблемами. `enabled = false` (інша розкладка, адреса ще не на якорі) — запиту немає, перелік
 * порожній.
 */
export function useYearUnmatchedRows(
  documentId: number,
  anchorPeriodKey: number,
  activeCopyId: number | null,
  enabled: boolean,
  anchorFinishedAt: string | null,
): readonly MonthUnmatchedRows[] {
  const originals = useYearCalculationIssues(documentId, anchorPeriodKey, null, enabled);
  const copy = useYearCalculationIssues(documentId, anchorPeriodKey, activeCopyId, enabled);

  return useMemo(
    () => (enabled ? splitIssuesByMonth(anchorPeriodKey, originals.data, copy.data, activeCopyId, anchorFinishedAt) : []),
    [enabled, anchorPeriodKey, originals.data, copy.data, activeCopyId, anchorFinishedAt],
  );
}
