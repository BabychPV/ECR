import { useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import {
  deleteRowLock,
  getRowLocks,
  postRowLocks,
  rowLocksKey,
  type CreateRowLocksRequest,
  type RowLockDto,
} from '@/features/documents/rowLocks/api';
import { invalidateSlices } from '@/features/grid/sliceCache';
import { showApiError } from '@/shared/ui/notify';

/**
 * ✎ B139: блокування рядків документа за період — ОДИН запит на документ і період (той самий ключ у кожної сітки
 * аркуша, TanStack зводить їх в один).
 */
export function useRowLocks(documentId: number, periodKey: number, enabled = true): UseQueryResult<RowLockDto[]> {
  return useQuery({
    queryKey: rowLocksKey(documentId, periodKey),
    queryFn: () => getRowLocks(documentId, periodKey),
    // Сітка поза сторінкою документа (без кодів аркуша й таблиці) блокувань не питає.
    enabled,
    // ⛔ Блокування — другорядні дані сторінки: відповідь не тієї форми (проксі, старий сервер) не має валити
    // сітку документа (`….filter is not a function` у рендері = помилка всього маршруту). Не масив — «блокувань
    // немає»; правку однаково вирішує сервер (`cellPermissions`).
    select: (data: RowLockDto[]) => (Array.isArray(data) ? data : []),
    // Блокування міняються рідко; свіжість тримає інвалідація після власних дій і перезапит при фокусі вікна.
    staleTime: 30_000,
  });
}

/**
 * Після створення чи зняття блокування: перелік і зрізи документа за період — `cellPermissions` (причина
 * `RowLocked`) рахує сервер, тож комірки змінюють стан лише з новим зрізом.
 */
async function refreshAfterChange(
  client: ReturnType<typeof useQueryClient>,
  documentId: number,
  periodKey: number,
): Promise<void> {
  await Promise.all([
    client.invalidateQueries({ queryKey: rowLocksKey(documentId, periodKey) }),
    invalidateSlices(client, { documentId, periodKey }),
  ]);
}

/** `POST …/row-locks`; відмова — `showApiError` (текст каталогу за кодом, без сирої подробиці сервера). */
export function useCreateRowLocks(documentId: number, periodKey: number, onCreated: () => void) {
  const client = useQueryClient();

  return useMutation({
    mutationFn: (body: CreateRowLocksRequest) => postRowLocks(documentId, body),
    onSuccess: async () => {
      onCreated();
      await refreshAfterChange(client, documentId, periodKey);
    },
    onError: (error: unknown) => showApiError(error),
  });
}

/** `DELETE …/row-locks/{lockId}`; відмова — `showApiError`. */
export function useReleaseRowLock(documentId: number, periodKey: number, onReleased: () => void) {
  const client = useQueryClient();

  return useMutation({
    mutationFn: (lockId: number) => deleteRowLock(documentId, lockId),
    onSuccess: async () => {
      onReleased();
      await refreshAfterChange(client, documentId, periodKey);
    },
    onError: (error: unknown) => showApiError(error),
  });
}
