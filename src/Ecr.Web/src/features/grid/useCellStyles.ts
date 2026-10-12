import { useMutation, useQueryClient } from '@tanstack/react-query';
import { notifications } from '@mantine/notifications';
import { apiFetch, EcrApiError } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { PatchCellStylesRequest, TableSliceDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { notificationCloseButtonProps } from '@/shared/ui/notify';
import { logSuppressedDetail, problemText } from '@/shared/ui/problemText';
import { applyStylePatches, chunkPatches, isValidPatch, type CellStylePatch } from './userCellStyle';

/**
 * Запис користувацького кольору комірок (L4): `PATCH …/tables/{tid}/cell-styles`
 * (`PatchCellStylesRequest`: `periodKey`, `items`, `reset`).
 *
 * Семантика сервера: `null`/відсутнє поле — не чіпати, `""` — зняти, `reset: true` —
 * зняти все оформлення з перелічених комірок.
 *
 * - Виділення понад стелю сервера (2000) йде ПОРЦІЯМИ ≤ 1000, послідовно.
 * - Оптимістичне оновлення — лише після перевірки значень (`isValidPatch`).
 * - Після відмови (і завжди після завершення) зріз інвалідується: відкат іде на
 *   АКТУАЛЬНИЙ стан сервера, а не на знімок, що міг застаріти (409, часткове
 *   застосування порцій). `previous` — лише миттєвий відкат до відповіді сервера.
 * - Тост відмови один: `onError` оголошений, глобальний обробник мовчить.
 */
export function useCellStyles(documentId: number, tableInstanceId: number, periodKey: number) {
  const queryClient = useQueryClient();
  const sliceKey = queryKeys.slices.one(tableInstanceId, periodKey);
  const path = `/api/v1/documents/${String(documentId)}/tables/${String(tableInstanceId)}/cell-styles`;

  return useMutation({
    mutationFn: async ({ cells, reset = false }: { cells: readonly CellStylePatch[]; reset?: boolean }) => {
      for (const chunk of chunkPatches(cells)) {
        const body: PatchCellStylesRequest = { periodKey, items: chunk, reset };

        await apiFetch<unknown>(path, { method: 'PATCH', body: JSON.stringify(body) });
      }
    },
    meta: { handled: true },
    onMutate: async ({ cells }) => {
      if (!cells.every(isValidPatch)) throw new Error('Invalid cell colour');

      await queryClient.cancelQueries({ queryKey: sliceKey });
      const previous = queryClient.getQueryData<TableSliceDto>(sliceKey);

      if (previous !== undefined) {
        queryClient.setQueryData<TableSliceDto>(sliceKey, applyStylePatches(previous, cells));
      }

      return { previous };
    },
    onError: (error, _variables, context) => {
      if (context?.previous !== undefined) queryClient.setQueryData(sliceKey, context.previous);

      const shown = problemText(error);
      const conflict = error instanceof EcrApiError && error.isConflict;

      logSuppressedDetail(shown);
      // `role="alert"` дає сам Mantine; `data-testid` — для e2e на справжньому стенді.
      notifications.show({
        color: conflict ? 'statusWarning' : 'statusError',
        message: conflict ? t('grid.cellColor.conflict') : (shown.detail ?? shown.title),
        closeButtonProps: notificationCloseButtonProps,
        ...{ 'data-testid': 'cell-color-toast' },
      });
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: sliceKey }),
  });
}
