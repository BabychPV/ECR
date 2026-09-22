import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { JobSummary } from '@/api/types';
import { recentJobsUrl } from './api';
import { activeJobCount } from './myTasks';

/*
 * ⛔ Адреса НЕ набирається тут іще раз: вона береться з `recentJobsUrl`
 * (`features/jobs/api.ts`), де й живе єдиний у клієнті літерал `/api/v1/jobs`.
 * Друга копія рядка — це друге місце, яке треба не забути правити, і перший же
 * привід для розбіжності між шапкою й екраном черги.
 */

/** Опитування, доки серед ВЛАСНИХ задач є `Queued`/`Running`. */
export const MyTasksActivePollMs = 3_000;

/**
 * Опитування, коли активних задач немає.
 *
 * ⛔ Не `false`, і це не марнотратство, а саме те, заради чого індикатор
 * існує. `useRecentJobs` зупиняє опитування на порожньому переліку — і має
 * рацію: екран `#/admin/jobs` відкривають, щоб подивитися на чергу ЗАРАЗ.
 * Шапка ж мусить ПОМІТИТИ появу задачі, якої ще секунду тому не було, —
 * зокрема поставленої з іншої вкладки або іншим пристроєм (`UX-09`: джерело —
 * сервер, тож індикатор переживає і вкладку, і рестарт). Зупинене опитування
 * означало б, що позначка з'явиться лише після наступної навігації.
 *
 * ⚠ 30 с — це той самий `staleTime` глобального клієнта запитів
 * (`app/queryClient.ts:52`), тобто в простої шапка не ходить на сервер
 * частіше, ніж будь-який інший запит застосунку вже вважає своє значення
 * застарілим.
 */
export const MyTasksIdlePollMs = 30_000;

/**
 * Власні фонові задачі для шухляди «My tasks» у шапці (`UI-07`, `BE-08`).
 *
 * ⛔ `mine=true` — НЕ клієнтський фільтр. Сервер бере власника з сеансу
 * (`JobsController.List`), і саме тому перелік власних задач доступний БЕЗ
 * права `System.ViewHealth` (`Q-156`). Фільтрування вже отриманого масиву за
 * `createdByUserId` не лише зайве — воно неможливе: без `mine` користувач без
 * права отримав би `403` ще до будь-якої фільтрації.
 *
 * ⚠ Ключ запиту — той самий `['jobs', true]`, що в `useRecentJobs(true)`:
 * шапка й екран черги дивляться на ОДНУ відповідь сервера, а не роблять два
 * запити на те саме питання. Відрізняється лише темп опитування.
 */
export function useMyTasks(): UseQueryResult<JobSummary[]> {
  return useQuery({
    queryKey: ['jobs', true],
    queryFn: () => apiFetch<JobSummary[]>(recentJobsUrl(true)),
    refetchInterval: (query) =>
      activeJobCount(query.state.data) > 0 ? MyTasksActivePollMs : MyTasksIdlePollMs,
  });
}
