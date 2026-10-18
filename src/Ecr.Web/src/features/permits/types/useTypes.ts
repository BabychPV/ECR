import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createPermitType, listPermitTypes } from '../api/typesApi';
import type { CreatePermitTypeRequest, PermitTypeListItem } from '../api/types';
import { typesKeys } from './typesKeys';

/** Розмір сторінки перегляду: максимум, який дозволяє сервер (`limit` 1..500). */
export const TypesPageSize = 500;

/** Запобіжник від курсору, що не рухається: більше сторінок за один перегляд не читаємо. */
const MaxPages = 100;

/**
 * Усі типи дозволів: сторінки читаються по черзі, доки сервер дає курсор.
 *
 * ⚠ Без фільтрів: статистика над таблицею («типів», «таблиць», «зі змінами») рахується по всьому
 * переліку, а пошук і категорія застосовуються на клієнті (`typesListModel`) — типів десятки, не тисячі.
 */
export async function fetchAllPermitTypes(): Promise<PermitTypeListItem[]> {
  const all: PermitTypeListItem[] = [];
  const seen = new Set<string>();
  let cursor: string | undefined;

  for (let page = 0; page < MaxPages; page += 1) {
    const result = await listPermitTypes({ limit: TypesPageSize, ...(cursor === undefined ? {} : { cursor }) });
    all.push(...result.items);

    const next = result.nextCursor;
    if (next === null || next.length === 0 || seen.has(next)) break;
    seen.add(next);
    cursor = next;
  }

  return all;
}

/** Перелік усіх типів дозволів (право `Registry.View` на сервері). */
export function useAllPermitTypes() {
  return useQuery({ queryKey: typesKeys.list(), queryFn: fetchAllPermitTypes });
}

/** Заводить тип чернеткою й скидає кеш типів; помилку (зокрема `409 ECR-PRM-4091`) віддає викликачу. */
export function useCreatePermitType() {
  const client = useQueryClient();

  return useMutation({
    mutationFn: (body: CreatePermitTypeRequest) => createPermitType(body),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: typesKeys.all });
    },
  });
}
