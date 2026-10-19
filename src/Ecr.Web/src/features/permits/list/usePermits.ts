import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  activatePermit,
  createPermit,
  deletePermit,
  endPermit,
  listPermits,
  renewPermit,
} from '../api/permitsApi';
import type { CreatePermitRequest, EndPermitRequest, Permit, PermitListItem } from '../api/types';

/** Ключі кешу переліку дозволів. Корінь `['permits','list']`: один `invalidateQueries` скидає перелік після будь-якої дії. */
export const permitsListKeys = {
  all: ['permits', 'list'] as const,
  list: () => ['permits', 'list', 'all'] as const,
};

/** Розмір сторінки: максимум, який дозволяє сервер (`limit` 1..500). */
export const PermitsPageSize = 500;

/** Запобіжник від курсору, що не рухається: більше сторінок за один перегляд не читаємо. */
const MaxPages = 100;

/**
 * Усі дозволи: сторінки читаються по черзі, доки сервер дає курсор.
 *
 * ⚠ Без фільтрів: показники над таблицею рахуються по всьому переліку, а пошук і фільтри застосовуються на
 * клієнті (`permitsListModel`) — дозволів сотні, не сотні тисяч, і сервер сам радить 500 на сторінку.
 */
export async function fetchAllPermits(): Promise<PermitListItem[]> {
  const all: PermitListItem[] = [];
  const seen = new Set<string>();
  let cursor: string | undefined;

  for (let page = 0; page < MaxPages; page += 1) {
    const result = await listPermits({ limit: PermitsPageSize, ...(cursor === undefined ? {} : { cursor }) });
    all.push(...result.items);

    const next = result.nextCursor;
    if (next === null || next.length === 0 || seen.has(next)) break;
    seen.add(next);
    cursor = next;
  }

  return all;
}

/** Перелік усіх дозволів (право `Registry.View` на сервері). */
export function useAllPermits() {
  return useQuery({ queryKey: permitsListKeys.list(), queryFn: fetchAllPermits });
}

/** Скидає перелік після дії; помилку віддає викликачу (`showApiError` у хості). */
function useInvalidateList(): () => Promise<void> {
  const client = useQueryClient();

  return () => client.invalidateQueries({ queryKey: permitsListKeys.all });
}

export function useCreatePermit() {
  const invalidate = useInvalidateList();

  return useMutation<Permit, Error, CreatePermitRequest>({
    mutationFn: (body) => createPermit(body),
    onSuccess: invalidate,
  });
}

export function useActivatePermit() {
  const invalidate = useInvalidateList();

  return useMutation<Permit, Error, number>({ mutationFn: (id) => activatePermit(id), onSuccess: invalidate });
}

export function useEndPermit() {
  const invalidate = useInvalidateList();

  return useMutation<Permit, Error, { readonly id: number; readonly body: EndPermitRequest }>({
    mutationFn: ({ id, body }) => endPermit(id, body),
    onSuccess: invalidate,
  });
}

export function useRenewPermit() {
  const invalidate = useInvalidateList();

  return useMutation<Permit, Error, number>({ mutationFn: (id) => renewPermit(id), onSuccess: invalidate });
}

export function useDeletePermit() {
  const invalidate = useInvalidateList();

  return useMutation<void, Error, number>({ mutationFn: (id) => deletePermit(id), onSuccess: invalidate });
}
