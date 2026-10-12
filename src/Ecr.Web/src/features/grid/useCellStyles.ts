import { useMutation, useQueryClient } from '@tanstack/react-query';
import { notifications } from '@mantine/notifications';
import { apiFetch, EcrApiError } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { PatchCellStylesRequest, PatchCellStylesResponse, TableSliceDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { notificationCloseButtonProps } from '@/shared/ui/notify';
import { logSuppressedDetail, problemText } from '@/shared/ui/problemText';
import { denyText } from './permissions';
import { applyStylePatches, chunkPatches, isValidPatch, type CellStylePatch, type StyleArea } from './userCellStyle';

function mergeResponses(a: PatchCellStylesResponse, b: PatchCellStylesResponse): PatchCellStylesResponse {
  const reasons: Record<string, number> = { ...(a.deniedByReason ?? {}) };

  for (const [reason, count] of Object.entries(b.deniedByReason ?? {})) reasons[reason] = (reasons[reason] ?? 0) + count;

  return {
    appliedCells: a.appliedCells + b.appliedCells,
    resetCells: a.resetCells + b.resetCells,
    deniedCells: a.deniedCells + b.deniedCells,
    skippedCells: a.skippedCells + b.skippedCells,
    deniedByReason: reasons,
  };
}

/**
 * Запис користувацького кольору комірок (L4): `PATCH …/tables/{tid}/cell-styles`
 * (`PatchCellStylesRequest`: `periodKey`, `items`, `reset`).
 *
 * Семантика сервера: `null`/відсутнє поле — не чіпати, `""` — зняти, `reset: true` —
 * зняти все оформлення з перелічених комірок.
 *
 * - Область «рядок/колонка/таблиця» (`area`) йде ОДНИМ запитом зі `scope` (сервер розгортає, стеля 50 000,
 *   часткова відмова — `deniedCells`); `cells` тоді лише для оптимістичного оновлення.
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
    mutationFn: async ({
      cells,
      areas = null,
      style = {},
      reset = false,
    }: {
      cells: readonly CellStylePatch[];
      /** Кольори для `scope` (`cells` порожні, коли область завелика для оптимістики). */
      style?: Pick<CellStylePatch, 'backgroundHex' | 'foregroundHex' | 'isBold'>;
      /** Області одного `scope` кожна (≤ 50 000 комірок, `areaChunks`); `null` — йдуть `items`. */
      areas?: readonly StyleArea[] | null;
      reset?: boolean;
    }): Promise<PatchCellStylesResponse | null> => {
      if (areas !== null) {
        let total: PatchCellStylesResponse | null = null;

        for (const area of areas) {
          const body: PatchCellStylesRequest = {
            periodKey,
            reset: false,
            scope: {
              ...area,
              reset,
              allRows: area.allRows ?? false,
              allColumns: area.allColumns ?? false,
              backgroundHex: style.backgroundHex ?? null,
              foregroundHex: style.foregroundHex ?? null,
              isBold: style.isBold ?? null,
            },
          };
          const response = await apiFetch<PatchCellStylesResponse>(path, { method: 'PATCH', body: JSON.stringify(body) });

          total = total === null ? response : mergeResponses(total, response);
        }

        return total;
      }

      for (const chunk of chunkPatches(cells)) {
        const body: PatchCellStylesRequest = { periodKey, items: chunk, reset };

        await apiFetch<unknown>(path, { method: 'PATCH', body: JSON.stringify(body) });
      }

      return null;
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
    onSuccess: (response) => {
      if (response === null || response.deniedCells <= 0) return;

      const reasons = Object.entries(response.deniedByReason ?? {})
        .map(([reason, count]) => `${denyText(reason) ?? reason}: ${String(count)}`)
        .join(', ');

      notifications.show({
        color: 'statusWarning',
        message: t('grid.cellColor.partial', { count: response.deniedCells, reasons }),
        closeButtonProps: notificationCloseButtonProps,
        ...{ 'data-testid': 'cell-color-toast' },
      });
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: sliceKey }),
  });
}
