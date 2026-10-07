import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { JobStatus } from '@/api/types';

/*
 * ⚠ Окремим модулем, а не в `api.ts`: `api.ts` тягнуть шапка («My tasks») і
 * екрани з фоновими задачами, тобто він у чанку КОЖНОГО маршруту, і зайвий
 * хук там ріс би бюджет усіх (`D-132`, PipelinePage біля межі 250 КБ).
 */
/** Як часто опитувати стан однієї задачі, поки вона виконується. */
const JobPollMs = 1500;

/**
 * Стан однієї задачі (`GET /jobs/{id}`) — для шторки задачі на `/admin/jobs`.
 *
 * ⚠ Опитування зупиняється, щойно задача завершилась: нескінченне опитування
 * завершеної задачі — запит на секунду від кожної відкритої вкладки. Батько-
 * розклад (P4) уже `Succeeded`, але дочірні ще рахуються — тоді опитування
 * триває.
 *
 * ⚠ Ключ `['job', id]` — той самий, що інвалідують скасування й перезапуск.
 * Сторінка (підвал шторки) і лінивий вміст шторки читають ОДИН запит.
 *
 * ⛔ `encodeURIComponent`: ідентифікатор буває з `#` і `~` (`smoke.ps1`, крок
 * 17), і без кодування `#` починав фрагмент адреси — `404`.
 */
export function useJobStatus(jobId: string | null): UseQueryResult<JobStatus> {
  return useQuery({
    queryKey: ['job', jobId],
    queryFn: () => apiFetch<JobStatus>(`/api/v1/jobs/${encodeURIComponent(jobId ?? '')}`),
    enabled: jobId !== null,
    refetchInterval: (query) => {
      const data = query.state.data;

      return data?.state === 'Queued' || data?.state === 'Running' || data?.effectiveState === 'FannedOut'
        ? JobPollMs
        : false;
    },
    retry: false,
  });
}
