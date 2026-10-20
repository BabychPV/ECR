import { useMemo } from 'react';
import { useMutation, useQuery, useQueryClient, type UseMutationResult } from '@tanstack/react-query';
import { changePermitListItems, getPermitList, listPermitLists, type PermitListChange, type PermitListDetail, type PermitListDto } from './listsApi';
import { listsKeys } from './listsKeys';
import { rowsFromLists, type ListRow } from './listsModel';

export interface ListsData {
  /** `undefined` — ще не приїхало (≠ «списків немає»). */
  readonly rows: readonly ListRow[] | undefined;
  readonly isPending: boolean;
  readonly error: Error | null;
  readonly refetch: () => void;
}

/** Перелік усіх списків вибору: ОДИН запит (`GET /permit-lists`), кеш спільний з формою рядка дозволу. */
export function usePermitListIndex(enabled = true) {
  return useQuery<PermitListDto[]>({
    queryKey: listsKeys.index,
    queryFn: listPermitLists,
    enabled,
  });
}

/** Дані екрана: перелік списків одним запитом, рядки — за моделлю. */
export function useLists(): ListsData {
  const query = usePermitListIndex();
  const rows = useMemo(() => (query.data === undefined ? undefined : rowsFromLists(query.data)), [query.data]);

  return {
    rows,
    isPending: query.isPending,
    error: query.error,
    refetch: () => {
      void query.refetch();
    },
  };
}

/** Значення списку для шторки (лише за відкритою шторкою; `null` — не читати). */
export function useListDetail(key: string | null) {
  return useQuery<PermitListDetail>({
    queryKey: listsKeys.detail(key ?? ''),
    queryFn: () => getPermitList(key ?? ''),
    enabled: key !== null,
  });
}

/** Префікс кешу значень пікерів у формі рядка дозволу (`permitKeys.list`): після зміни значень їх треба перечитати. */
const PickerKeys = ['permits', 'pick'] as const;

/**
 * Зміна значень списку (`PUT /permit-lists/{key}/items`). Успіх кладе нову відповідь у кеш шторки, скидає перелік
 * (лічильники) і значення пікерів форми дозволу; готова відповідь доступна й виклику (`mutateAsync`).
 */
export function useChangeListItems(key: string): UseMutationResult<PermitListDetail, Error, readonly PermitListChange[]> {
  const client = useQueryClient();

  return useMutation<PermitListDetail, Error, readonly PermitListChange[]>({
    mutationKey: ['permits', 'lists', 'change', key],
    mutationFn: (changes) => changePermitListItems(key, changes),
    onSuccess: async (detail) => {
      client.setQueryData(listsKeys.detail(key), detail);
      await Promise.all([
        client.invalidateQueries({ queryKey: listsKeys.index }),
        client.invalidateQueries({ queryKey: PickerKeys }),
      ]);
    },
  });
}
