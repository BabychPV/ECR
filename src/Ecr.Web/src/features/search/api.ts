import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/*
 * ⛔ Адреса записана повністю поруч із `apiFetch` — сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає саме літерал `/api/v1/…`.
 */

/** Один збіг пошуку даних палітри (BE-19). Маршрут будує клієнт за `kind`. */
export type SearchHit = components['schemas']['SearchHitDto'];

/** Той самий мінімум, що на сервері: коротший запит не йде в мережу взагалі. */
export const searchMinLength = 2;

/**
 * Документи, шаблони й довідники за підрядком коду чи назви — лише видимі
 * користувачу (права й гранти перевіряє сервер).
 */
export function searchData(query: string, limit?: number): Promise<SearchHit[]> {
  const term = query.trim();
  if (term.length < searchMinLength) {
    return Promise.resolve([]);
  }

  const params = new URLSearchParams({ q: term });
  if (limit !== undefined) {
    params.set('limit', String(limit));
  }

  return apiFetch<SearchHit[]>(`/api/v1/search?${params.toString()}`);
}

/** Хук для палітри: вимкнений, доки запит коротший за мінімум. */
export function useDataSearch(query: string): UseQueryResult<SearchHit[]> {
  const term = query.trim();

  return useQuery({
    queryKey: ['search', term],
    queryFn: () => searchData(term),
    enabled: term.length >= searchMinLength,
    staleTime: 30_000,
  });
}
