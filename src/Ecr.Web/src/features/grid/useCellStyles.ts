import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { TableSliceDto } from '@/api/types';
import { showApiError } from '@/shared/ui/notify';
import { applyStylePatches, type CellStylePatch, type SliceWithUserStyles } from './userCellStyle';

/**
 * Запис користувацького кольору комірок (L4): `PATCH …/tables/{tid}/cell-styles`.
 *
 * Оптимістично: колір лягає в кеш зрізу одразу (єдиний ключ `queryKeys.slices.one`),
 * відмова повертає кеш як був і показує тост ОДИН раз — `onError` оголошений, тож
 * глобальний обробник мовчить; `meta.handled` додано для читача.
 */
export function useCellStyles(documentId: number, tableInstanceId: number, periodKey: number) {
  const queryClient = useQueryClient();
  const sliceKey = queryKeys.slices.one(tableInstanceId, periodKey);

  return useMutation({
    mutationFn: (cells: readonly CellStylePatch[]) =>
      apiFetch<unknown>(`/api/v1/documents/${String(documentId)}/tables/${String(tableInstanceId)}/cell-styles`, {
        method: 'PATCH',
        body: JSON.stringify({ cells }),
      }),
    meta: { handled: true },
    onMutate: async (cells) => {
      await queryClient.cancelQueries({ queryKey: sliceKey });
      const previous = queryClient.getQueryData<TableSliceDto>(sliceKey);

      if (previous !== undefined) {
        queryClient.setQueryData<SliceWithUserStyles>(sliceKey, applyStylePatches(previous, cells));
      }

      return { previous };
    },
    onError: (error, _cells, context) => {
      if (context?.previous !== undefined) queryClient.setQueryData(sliceKey, context.previous);
      showApiError(error);
    },
  });
}
