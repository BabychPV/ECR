import { useMemo } from 'react';
import { useQueries, type UseQueryResult } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { components } from '@/api/schema';
import type { RegistryEntryDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';

type RegistryEntryDetailDto = components['schemas']['RegistryEntryDetailDto'];

/**
 * PS-P2 (D-PS-6): закритий запис довідника не доходить до переліку на дату
 * (`GET …/entries?asOf=` віддає лише чинні), а в комірці/шапці лежить його id.
 * Раніше показувався сирий id. Тут назва добирається окремо — тим самим
 * `GET …/entries/{id}` (контракт не змінено), — і лише для id, яких у переліку
 * немає. Перелік для ВИБОРУ при цьому не міняється: закриті записи в ньому
 * не з'являються, тож для нового вибору вони недоступні.
 */

/** Запит на добір: довідник і id, яких немає в його переліку на дату. */
export interface UnlistedLookupRequest {
  readonly registryId: number;
  readonly code: string;
  readonly ids: readonly number[];
}

/** Обмеження на довідник: сторінка з тисячами різних id не має породжувати шторм запитів. */
const MaxIdsPerRegistry = 200;

const StaleTimeMs = 5 * 60_000;

/**
 * Які id треба добирати: значення `values`, що є числами, але не записами `entries`.
 * `entries === undefined` (перелік ще їде) — нічого не добираємо, щоб не питати зайвого.
 */
export function unlistedIdsOf(
  values: Iterable<unknown>,
  entries: readonly RegistryEntryDto[] | undefined,
): number[] {
  if (entries === undefined) return [];

  const known = new Set<number>();
  for (const entry of entries) known.add(entry.id);

  const wanted = new Set<number>();
  for (const value of values) {
    const id =
      typeof value === 'number'
        ? value
        : typeof value === 'string' && value.trim().length > 0
          ? Number(value)
          : Number.NaN;
    if (Number.isInteger(id) && id > 0 && !known.has(id)) wanted.add(id);
    if (wanted.size >= MaxIdsPerRegistry) break;
  }

  return [...wanted].sort((a, b) => a - b);
}

/** Підпис запису: назва + позначка «закрито», якщо в запису є кінець чинності. */
export function unlistedEntryLabel(detail: RegistryEntryDetailDto): string {
  const name = localized(detail.displayL10n) || detail.code;

  return detail.validTo === null ? name : t('grid.lookupClosedEntry', { name });
}

interface Flat {
  readonly registryId: number;
  readonly id: number;
  readonly code: string;
}

function combineLabels(results: UseQueryResult<RegistryEntryDetailDto>[]): (RegistryEntryDetailDto | undefined)[] {
  return results.map((result) => result.data);
}

/**
 * Підписи закритих/позаперелікових записів: `registryId → (id → підпис)`.
 * Не знайдений чи відмовлений запис просто відсутній — показ лишається сирим id
 * (`lookupCellDisplay`), а не порожнечею.
 */
export function useUnlistedLookupLabels(
  requests: readonly UnlistedLookupRequest[],
): ReadonlyMap<number, ReadonlyMap<number, string>> {
  const flat = useMemo<Flat[]>(
    () =>
      requests.flatMap((request) =>
        request.ids.map((id) => ({ registryId: request.registryId, id, code: request.code })),
      ),
    [requests],
  );

  const details = useQueries({
    queries: flat.map(({ code, id }) => ({
      queryKey: [...queryKeys.registries.entries(code), 'unlisted', id],
      queryFn: () =>
        apiFetch<RegistryEntryDetailDto>(
          `/api/v1/registries/${encodeURIComponent(code)}/entries/${String(id)}`,
        ),
      staleTime: StaleTimeMs,
      retry: false,
    })),
    combine: combineLabels,
  });

  return useMemo(() => {
    const byRegistry = new Map<number, Map<number, string>>();
    flat.forEach(({ registryId, id }, index) => {
      const detail = details[index];
      if (detail === undefined) return;

      let labels = byRegistry.get(registryId);
      if (labels === undefined) {
        labels = new Map();
        byRegistry.set(registryId, labels);
      }
      labels.set(id, unlistedEntryLabel(detail));
    });

    return byRegistry;
  }, [flat, details]);
}
