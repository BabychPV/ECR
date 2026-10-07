import { useQuery } from '@tanstack/react-query';
import type { CalculationResultDto } from '@/api/types';
import { apiFetch } from '@/api/client';
import { calculationResultsKey } from './calculationResultsKey';

/**
 * Чи застарілі числа методологій документа: сервер позначив їх `isStale` (входи змінилися після
 * прогону, F-05) або після прогону змінено довідник, який читає методологія (`changedRegistries`, RT-25).
 *
 * ⚠ Той самий критерій, що й у банера `CalculationResultsPanel`: бейдж у шапці й банер під сіткою
 * не повинні розходитися.
 */
export function calculationsAreStale(list: readonly CalculationResultDto[] | null | undefined): boolean {
  // ⚠ `Array.isArray`: до відповіді (і на порожнє тіло) даних немає — «не знаємо» не є «застаріло».
  return Array.isArray(list) && list.some((r) => r.isStale || (r.changedRegistries ?? []).length > 0);
}

/**
 * Застарілість чисел методологій для рядка дій документа.
 *
 * ⛔ Ключ — той самий, що в панелі чисел (`calculationResultsKey`): одне джерело правди й один запит,
 * а завершений перерахунок інвалідує `['document', id, period]` і перечитує обох разом. Адреса —
 * та сама, що в `calculationResults` (`api.ts`), але без імпорту цього модуля: він тягне весь API
 * методологій у чанк сторінки документа (бюджет `D-132`).
 *
 * ⚠ `enabled` — право `Calculation.View`: без нього сервер відповів би 403, а бейдж не варто малювати.
 */
export function useCalculationsStale(documentId: number, periodKey: number, enabled: boolean): boolean {
  const results = useQuery({
    queryKey: calculationResultsKey(documentId, periodKey),
    queryFn: () =>
      apiFetch<CalculationResultDto[]>(
        `/api/v1/documents/${String(documentId)}/calculation-results?periodKey=${String(periodKey)}`,
      ),
    enabled,
    retry: false,
  });

  return calculationsAreStale(results.data);
}
