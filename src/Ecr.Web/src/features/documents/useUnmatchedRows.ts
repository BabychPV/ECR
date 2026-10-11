import { useQuery } from '@tanstack/react-query';
import type { CalculationRunStatusDto } from '@/api/types';
import { apiFetch } from '@/api/client';
import { calculationResultsKey } from '@/features/methodologies/calculationResultsKey';

/** Рядки без правила розрахунку з профілю актуального прогону (D14); `null` - таких немає або діагностика недоступна. */
export type UnmatchedRows = NonNullable<CalculationRunStatusDto['unmatchedRows']>;

/**
 * Рядки документа, яким не підійшло жодне правило розрахунку (D14): інформація для банера на аркуші.
 *
 * ⛔ Ключ - `calculationResultsKey(...)` + `'run'`: завершений перерахунок інвалідує `['document', id, period,
 * 'calculation-results']` за префіксом і перечитує банер разом із числами; окремого ключа не було б у предикаті
 * `isCalculationResultsQuery`, і банер лишався б від старого прогону.
 *
 * ⚠ `enabled` - право `Calculation.View`: без нього сервер відповів би 403. Помилка читання діагностики не повинна
 * ламати документ, тому `null`, а не виняток; повторів немає.
 */
export function useUnmatchedRows(documentId: number, periodKey: number, enabled: boolean): UnmatchedRows | null {
  const status = useQuery({
    queryKey: [...calculationResultsKey(documentId, periodKey), 'run'],
    queryFn: () =>
      apiFetch<CalculationRunStatusDto>(
        `/api/v1/documents/${String(documentId)}/calculation-run?periodKey=${String(periodKey)}`,
      ),
    enabled,
    retry: false,
  });

  const rows = status.data?.unmatchedRows ?? null;
  return rows !== null && rows.count > 0 ? rows : null;
}