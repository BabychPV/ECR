import { useMemo } from 'react';
import { useQueries, useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { RegistryEntryDto } from '@/api/types';
import { getPermitType } from '../api/typesApi';
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
  readonly refetch: () => void;
}

/**
 * Дані екрана: перелік типів → визначення кожного типу (колонки зі списками) + спільні довідники.
 * ⚠ Ключ `typesKeys.one` той самий, що в конструкторі, тож відкритий раніше тип не читається вдруге.
 */
export function useLists(): ListsData {
  const list = useAllPermitTypes();
  const codes = (list.data ?? []).map((item) => item.code);
  const definitions = useQueries({
    queries: codes.map((code) => ({ queryKey: typesKeys.one(code), queryFn: () => getPermitType(code) })),
  });
  const registries = useSharedRegistries(true);

  const loaded = definitions.every((query) => query.data !== undefined);
  const types = definitions.map((query) => query.data);
  const stamp = definitions.map((query) => query.dataUpdatedAt).join(',');
  const rows = useMemo(
    () =>
      list.data === undefined || !loaded
        ? undefined
        : buildListRows(
            types.filter((type) => type !== undefined),
            registries.data ?? [],
          ),
    // Відбиток часу оновлення визначень замінює масив, що перебудовується щорендера.
    [list.data, loaded, registries.data, stamp],
  );
  const failed = definitions.find((query) => query.error !== null)?.error ?? null;

  return {
    rows,
    isPending: list.isPending || (!loaded && failed === null && list.error === null) || registries.isPending,
    error: list.error ?? failed,
    registriesFailed: registries.isError,
    refetch: () => {
      void list.refetch();
      definitions.forEach((query) => void query.refetch());
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
