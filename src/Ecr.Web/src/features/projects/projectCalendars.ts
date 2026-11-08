import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { PeriodCalendarDto } from '@/api/types';

/** Скільки календарів іде одночасно — та сама межа, що й у `useProjectCalendars` (N4-08). */
const Concurrency = 4;

/** Календарі проєктів пачками по {@link Concurrency}; відмова одного — `undefined` для нього, а не відмова всіх. */
export async function fetchCalendars(projectIds: readonly number[]): Promise<(PeriodCalendarDto | undefined)[]> {
  const result: (PeriodCalendarDto | undefined)[] = [];
  for (let start = 0; start < projectIds.length; start += Concurrency) {
    const batch = projectIds.slice(start, start + Concurrency);
    const answers = await Promise.allSettled(
      batch.map((id) => apiFetch<PeriodCalendarDto>(`/api/v1/projects/${String(id)}/periods`)),
    );
    result.push(...answers.map((answer) => (answer.status === 'fulfilled' ? answer.value : undefined)));
  }

  return result;
}

/**
 * ✎ g1/g3 (макет 08.11): календарі ВСІХ видимих проєктів для переліку документів — роки фільтра «Year» і стан
 * періоду в смузі 12 місяців. Один запит-обгортка замість `useQueries`/`useProjectCalendars`: маршрут DocumentsPage
 * стоїть біля межі `D-132` (250 КБ gzip), а модуль `periodStates` тягнув би форматування дат, якого тут не треба.
 */
export function useCalendarsOf(projectIds: readonly number[]): readonly (PeriodCalendarDto | undefined)[] {
  const key = projectIds.join(',');
  const query = useQuery({
    queryKey: ['periods', 'many', key],
    queryFn: () => fetchCalendars(projectIds),
    enabled: projectIds.length > 0,
    staleTime: 60_000,
  });

  return query.data ?? [];
}
