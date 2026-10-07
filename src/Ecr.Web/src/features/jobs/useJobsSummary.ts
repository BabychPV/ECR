import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/** Лічильники черги для смуги показників `/admin/jobs` (`GET /jobs/summary`). */
export type JobsSummary = components['schemas']['JobsSummary'];

/** Як часто оновлювати лічильники, поки екран відкритий. */
const SummaryPollMs = 10_000;

/**
 * Лічильники черги (UI-28, LS-F).
 *
 * ⛔ Смуга — з СЕРВЕРА, а не з переліку: перелік — лише останні задачі, і лічба
 * по ньому видавала б себе за стан усієї черги, а «провалені за добу» з нього
 * не порахувати взагалі.
 *
 * ⚠ `mine` — та сама межа, що в переліку: без права `System.ViewHealth`
 * сервер віддає лише власні (`?mine=true`), інакше `403`.
 *
 * ⚠ Окремим модулем, а не в `api.ts`: `api.ts` у чанку кожного маршруту
 * (шапка «My tasks»), і цей хук ріс би бюджет усіх (`D-132`).
 */
export function useJobsSummary(mine: boolean): UseQueryResult<JobsSummary> {
  return useQuery({
    queryKey: ['jobs-summary', mine],
    queryFn: () => apiFetch<JobsSummary>(mine ? '/api/v1/jobs/summary?mine=true' : '/api/v1/jobs/summary'),
    refetchInterval: SummaryPollMs,
  });
}
