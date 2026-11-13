import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { ConsistencyIssuesKey } from '@/features/jobs/useConsistencyRun';

/**
 * Коди задачі перевірки узгодженості в черзі: нічний розклад ставить конкретний клас, ручний запуск — порт
 * (`ConsistencyCheckJob` remarks), тож «остання перевірка» — свіжіша з двох.
 */
const CheckJobCodes = ['Ecr.Infrastructure.Jobs.ConsistencyCheckJob', 'Ecr.Application.Ports.IConsistencyCheckJob'];

/** Найсвіжіший `updatedAt` серед відповідей переліку задач; `null` — немає жодного успішного прогону. */
export function latestCheckTime(lists: readonly unknown[]): string | null {
  let latest: string | null = null;

  for (const list of lists) {
    if (!Array.isArray(list)) continue;

    for (const job of list as readonly unknown[]) {
      const updatedAt = (job as { updatedAt?: unknown } | null)?.updatedAt;
      if (typeof updatedAt !== 'string') continue;
      if (latest === null || Date.parse(updatedAt) > Date.parse(latest)) latest = updatedAt;
    }
  }

  return latest;
}

/**
 * Момент останньої УСПІШНОЇ перевірки узгодженості (рішення людини 12.11: журнал — знімок на момент перевірки,
 * тож екран мусить казати, на який момент).
 *
 * ⚠ З переліку задач (`GET /api/v1/jobs?code=…&state=Succeeded&limit=1`, право `System.ViewHealth` — те саме, що
 * відкриває екран), без нового поля в контракті. Записи прогресу мають ретенцію: коли їх уже прибрано, рядка
 * «остання перевірка» немає, а не вигаданий час. Ключ — під `ConsistencyIssuesKey`, тож успішний «Run check now»
 * оновлює і його.
 */
export function useLastConsistencyCheck(): string | null {
  const query = useQuery({
    queryKey: [...ConsistencyIssuesKey, 'last-check'],
    queryFn: async () =>
      latestCheckTime(
        await Promise.all(
          CheckJobCodes.map((code) =>
            apiFetch<unknown>(`/api/v1/jobs?code=${encodeURIComponent(code)}&state=Succeeded&limit=1`),
          ),
        ),
      ),
    retry: false,
  });

  return query.data ?? null;
}
