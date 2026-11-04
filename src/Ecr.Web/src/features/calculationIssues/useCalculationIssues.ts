import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import { calculationIssuesKey, getCalculationIssues, type CalculationIssues } from './api';

/** Невидимий документ (`404`): проблем для нього не показують зовсім - ні списку, ні помилки. */
export function isNotFound(error: unknown): boolean {
  return error instanceof EcrApiError && error.problem.status === 404;
}

/**
 * Проблеми розрахунку активного аркуша: `sheetCopyId` - копія (`null` - оригінал).
 *
 * Запит читання, не мутація: помилка тут не сповіщає глобально, її малює споживач (панель) або мовчки
 * ігнорує (позначка в дереві). `404` не повторюється. Той самий ключ читають панель сторінки й дерево таблиць -
 * це один запит.
 */
export function useCalculationIssues(
  documentId: number,
  periodKey: number,
  sheetCopyId: number | null,
): UseQueryResult<CalculationIssues> {
  return useQuery({
    queryKey: calculationIssuesKey(documentId, periodKey, sheetCopyId),
    queryFn: () => getCalculationIssues(documentId, periodKey, sheetCopyId),
    retry: (failures, error) => !isNotFound(error) && failures < 1,
  });
}
