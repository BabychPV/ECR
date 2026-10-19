import { useMemo } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { RegistryEntryDto } from '@/api/types';
import { getPermitType } from '../api/typesApi';
import type { PermitType } from '../api/types';
import { useSharedRegistries } from '../typeEditor/lookups';
import { typesKeys } from '../types/typesKeys';
import { useAllPermitTypes } from '../types/useTypes';
import { buildListRows, type ListRow } from './listsModel';
import { listsKeys } from './listsKeys';

export interface ListsData {
  /** `undefined` — ще не приїхало (≠ «списків немає»). */
  readonly rows: readonly ListRow[] | undefined;
  readonly isPending: boolean;
  readonly error: Error | null;
  /** Довідники не прочитались (немає `Registry.View` чи збій): списки показані за кодами. */
  readonly registriesFailed: boolean;
  /** Коди типів, визначення яких не прочиталось: екран живий, їхніх списків просто немає. */
  readonly failedTypes: readonly string[];
  readonly refetch: () => void;
}

/** Скільки визначень типів читається одночасно (ліміт запитів — 100/хв на користувача). */
export const TypeFetchConcurrency = 4;

/** Свіжість визначення в кеші: відкритий щойно конструктор не читається вдруге. */
const DefinitionFreshMs = 30_000;

interface Definitions {
  readonly types: PermitType[];
  readonly failed: string[];
}

/** Виконує `task` над елементами не більш як `limit` одночасно; порядок результатів збережено. */
async function inPool<T, R>(items: readonly T[], limit: number, task: (item: T) => Promise<R>): Promise<R[]> {
  const results = new Array<R>(items.length);
  let next = 0;
  const worker = async (): Promise<void> => {
    while (next < items.length) {
      const index = next;
      next += 1;
      results[index] = await task(items[index] as T);
    }
  };
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, worker));

  return results;
}

/**
 * Дані екрана: перелік типів → визначення типів (колонки зі списками, по `TypeFetchConcurrency` одночасно,
 * зі спільного кешу `typesKeys.one`, якщо він свіжий) + спільні довідники. Тип, який не прочитався, не валить
 * екран: його код іде в `failedTypes`.
 */
export function useLists(): ListsData {
  const client = useQueryClient();
  const list = useAllPermitTypes();
  const codes = (list.data ?? []).map((item) => item.code);
  const definitions = useQuery({
    queryKey: listsKeys.definitions(codes),
    enabled: list.data !== undefined,
    queryFn: async (): Promise<Definitions> => {
      const read = await inPool(codes, TypeFetchConcurrency, async (code) => {
        try {
          return await client.fetchQuery({
            queryKey: typesKeys.one(code),
            queryFn: () => getPermitType(code),
            staleTime: DefinitionFreshMs,
          });
        } catch {
          return code;
        }
      });

      return {
        types: read.filter((item): item is PermitType => typeof item !== 'string'),
        failed: read.filter((item): item is string => typeof item === 'string'),
      };
    },
  });
  const registries = useSharedRegistries(true);

  const rows = useMemo(
    () =>
      definitions.data === undefined ? undefined : buildListRows(definitions.data.types, registries.data ?? []),
    [definitions.data, registries.data],
  );

  return {
    rows,
    isPending: list.isPending || definitions.isPending || registries.isPending,
    error: list.error ?? definitions.error,
    registriesFailed: registries.isError,
    failedTypes: definitions.data?.failed ?? [],
    refetch: () => {
      void list.refetch();
      void definitions.refetch();
      void registries.refetch();
    },
  };
}
/** Сьогодні, UTC (`YYYY-MM-DD`): довідники темпоральні, значення беруться на дату. */
function today(): string {
  return new Date().toISOString().slice(0, 10);
}

/** Чинні значення спільного довідника для шторки (лише за відкритою шторкою). */
export function useListEntries(registryCode: string | null) {
  return useQuery({
    queryKey: listsKeys.entries(registryCode ?? ''),
    queryFn: () =>
      apiFetch<RegistryEntryDto[]>(
        `/api/v1/registries/${encodeURIComponent(registryCode ?? '')}/entries?asOf=${today()}`,
      ),
    enabled: registryCode !== null,
  });
}
