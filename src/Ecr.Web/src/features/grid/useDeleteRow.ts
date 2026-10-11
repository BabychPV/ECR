import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { DeleteRowResponse, RowDto } from '@/api/types';
import { showApiError } from '@/shared/ui/notify';
import { addRowRetryDelayMs, shouldRetryAddRow } from './useAddRow';
import { followRecalculation } from './useCellPatch';

/**
 * Чи можна цей рядок видалити з інтерфейсу (D3-A).
 *
 * ⛔ Лише рядки, які додав користувач: сервер віддає `rowKind` = режим таблиці (`Dynamic` / `Mixed`) для
 * рядка без опису в шаблоні і `rowKind` опису (`Item`, `Group`…) для рядка шаблону. Це підказка кнопці, а не
 * захист: сервер однаково відмовляє `409 ECR-ROW-0409` на рядок шаблону.
 */
export function isDeletableRow(row: Pick<RowDto, 'rowKind'> | undefined): boolean {
  return row !== undefined && (row.rowKind === 'Dynamic' || row.rowKind === 'Mixed');
}

/**
 * М'яке видалення рядка динамічної таблиці (`D3-A`).
 *
 * ⚠ Повтор — та сама політика, що в `useAddRow`: лише минуща `409 sheetBeingSubmitted` (нічого не
 * записано). Відмова «рядок шаблону / подано / немає прав» повтором не лікується й показується як є.
 *
 * ⚠ Перерахунок: видалення міняє підсумки по колонках, тому відповідь несе `recalculationJobId`, і сітка
 * стежить за ним тим самим шляхом, що й за PATCH (`followRecalculation`) — без перезапиту зрізу на кожне
 * опитування.
 */
export function useDeleteRow(
  documentId: number,
  tableInstanceId: number,
  periodKey: number,
  onDeleted: () => void,
) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (rowKey: string) =>
      apiFetch<DeleteRowResponse>(
        `/api/v1/documents/${String(documentId)}/tables/${String(tableInstanceId)}/rows/${encodeURIComponent(rowKey)}`,
        { method: 'DELETE' },
      ),
    onSuccess: (response) => {
      const jobId = response.recalculationJobId;
      if (typeof jobId === 'string' && jobId.length > 0) {
        followRecalculation(queryClient, jobId, documentId, periodKey);
      }
      onDeleted();
    },
    retry: shouldRetryAddRow,
    retryDelay: addRowRetryDelayMs,
    onError: showApiError,
  });
}
