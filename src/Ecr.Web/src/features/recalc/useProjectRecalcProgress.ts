import { useEffect, useRef } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { JobSummary } from '@/api/types';
import { markSlicesStale } from '@/features/grid/sliceCache';
import { isProjectRecalculation, isRecalcActive, progressOf, type RecalcProgress } from './recalcProgress';

/** Опитування, поки перерахунок іде. */
export const RecalcPollMs = 3_000;

/** Скільки останніх перерахунків проєктів читає банер без обраного проєкту (перелік документів «All projects»). */
export const AnyProjectLimit = 5;

/**
 * Адреса останніх ВЛАСНИХ перерахунків проєкту (або будь-якого проєкту).
 *
 * ⚠ `mine=true` — рішення координатора 08.11: банер бачить ініціатор (сценарій a1–a6 перевіряє саме його), і це не
 * вимагає `System.ViewHealth`. `kind=projectRecalculation` — лише батьки розкладу, без 92 документних дітей.
 */
export function projectRecalcUrl(projectId: number | null): string {
  return projectId === null
    ? `/api/v1/jobs?mine=true&kind=projectRecalculation&limit=${String(AnyProjectLimit)}`
    : `/api/v1/jobs?mine=true&kind=projectRecalculation&projectId=${String(projectId)}&limit=1`;
}

/**
 * Рядки переліку; будь-що, крім масиву (старий сервер, заглушка відповіді, проксі-сторінка), — порожньо.
 * ⚠ Банер не має права покласти сторінку, на якій змонтований (документ, перелік, Periods).
 */
export function rowsOf(data: unknown): JobSummary[] {
  return Array.isArray(data) ? (data as JobSummary[]) : [];
}

/** Ключ запиту; префікс `['jobs']` — інвалідації переліку задач (повтор, скасування) зачіпають і його. */
export function projectRecalcKey(projectId: number | null): readonly unknown[] {
  return ['jobs', 'project-recalc', projectId];
}

/**
 * Перерахунки проєкту з СЕРВЕРА — щоб сторінка, на яку повернулись (перелік документів, Periods, документ), бачила
 * прогрес, а не лише «Мої задачі» (людина 08.11: «якщо я залишу сторінку з документами і повернуся я не бачу
 * прогресу»).
 *
 * ⚠ Самодостатній: жодного стану сторінки, лише `projectId` (`null` — будь-який власний). Тому банер можна
 * перенести на вкладку Periods майбутньої сторінки проєкту без змін.
 *
 * ⛔ Кеш сіток і перелік документів скидаються на ПЕРЕХОДІ «йде → завершено», побаченому цим хуком, а не на
 * постановці в чергу: до завершення дітей числа ще старі.
 */
export function useProjectRecalcProgress(projectId: number | null, enabled = true): {
  readonly items: readonly RecalcProgress[];
  readonly isPending: boolean;
} {
  const queryClient = useQueryClient();

  const query = useQuery({
    queryKey: projectRecalcKey(projectId),
    queryFn: () => apiFetch<JobSummary[]>(projectRecalcUrl(projectId)),
    enabled,
    refetchInterval: (q) =>
      rowsOf(q.state.data).filter(isProjectRecalculation).map(progressOf).some(isRecalcActive) ? RecalcPollMs : false,
    // ⚠ Без права на перелік (чужа сесія, анонім) банера просто немає — не червоне.
    retry: false,
  });

  const items = rowsOf(query.data).filter(isProjectRecalculation).map(progressOf);

  // Перехід «йде → завершено» по кожній задачі.
  const seenActive = useRef(new Set<string>());
  const stamp = items.map((item) => `${item.jobId}:${item.phase}`).join(',');

  useEffect(() => {
    let finished = false;

    for (const item of items) {
      if (isRecalcActive(item)) {
        seenActive.current.add(item.jobId);
      } else if (seenActive.current.delete(item.jobId)) {
        finished = true;
      }
    }

    if (finished) {
      void markSlicesStale(queryClient);
      void queryClient.invalidateQueries({ queryKey: ['document'] });
      void queryClient.invalidateQueries({ queryKey: ['documents'] });
    }
    // ⚠ Лише `stamp`: він описує `items` повністю (масив новий щорендера).
  }, [stamp]);

  return { items, isPending: enabled && query.isPending };
}
