import { useEffect, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { JobStatus } from '@/api/types';
import { outcomeOf, pollInterval, type JobOutcome } from '@/features/workflow/jobFollow';
import { runConsistencyCheck } from './api';

/**
 * Право, яке вимагає `POST /api/v1/consistency/run`
 * (`RunConsistencyCheckHandler.Permission`).
 *
 * ⚠ Не `System.ViewHealth`, яким відкривається сам екран: журнал читає
 * більше людей, ніж може ставити повний обхід партицій у чергу.
 */
export const RunConsistencyPermission = 'System.RunJob';

/** Ключ переліку знахідок — те, що перечитується після успішного прогону. */
export const ConsistencyIssuesKey = ['consistency-issues'] as const;

/** Стан ручного прогону перевірки узгодженості для екрана. */
export interface ConsistencyRun {
  /** Поставити перевірку в чергу з причиною. */
  start: (reason: string) => void;

  /** Запит постановки ще в дорозі. */
  isStarting: boolean;

  /** Відмова ПОСТАНОВКИ (не задачі); `null` — відмови не було. */
  startError: unknown;

  /** Підсумок стеження; `null` — у цьому сеансі екрана нічого не ставили. */
  outcome: JobOutcome | null;

  /** Текст сервера про збій задачі, якщо він є. */
  failure: string | null;
}

/**
 * Прогін перевірки узгодженості «зараз» і стеження за ним.
 *
 * ⛔ Правило опитування і підсумку — спільні (`jobFollow.ts`), власного тут
 * немає. Так само немає власного таймера «здаємося»: після серверної правки
 * ретраї задачі сплять 30 → 60 → 120 с, і до `Failed` може минути ~210 с.
 * Клієнтська межа, коротша за цю, показала б «не вдалося» на задачі, яка ще
 * йде, — тобто неправду про стан сервера.
 *
 * ⛔ «Стан прочитати не вдалося» (`GET /jobs/{id}` вимагає окремого права) —
 * це `unknown`, а не «виконується»: `retry: false` і `outcomeOf(…, isError)`.
 *
 * ⚠ Перелік знахідок перечитується ОДИН раз на задачу, на кінцевому
 * `Succeeded`, а не на відповіді `202`: у момент постановки перевірка ще нічого
 * не записала (той самий дефект, який пройшов `SnapshotsPage`).
 */
export function useConsistencyRun(): ConsistencyRun {
  const queryClient = useQueryClient();
  const [jobId, setJobId] = useState<string | null>(null);

  const enqueue = useMutation({
    mutationFn: runConsistencyCheck,
    onSuccess: (job) => setJobId(job.jobId),
  });

  const job = useQuery({
    queryKey: ['job', jobId],
    queryFn: () => apiFetch<JobStatus>(`/api/v1/jobs/${encodeURIComponent(jobId ?? '')}`),
    enabled: jobId !== null,
    refetchInterval: (query) => pollInterval(query.state.data?.state),
    retry: false,
  });

  const outcome = jobId === null ? null : outcomeOf(job.data?.state, job.isError);

  // ⚠ `ref`, не стан: перечитати перелік рівно раз на задачу, а не на кожен рендер.
  const reported = useRef<string | null>(null);

  useEffect(() => {
    if (jobId === null || outcome !== 'succeeded' || reported.current === jobId) return;

    reported.current = jobId;
    void queryClient.invalidateQueries({ queryKey: ConsistencyIssuesKey });
  }, [jobId, outcome, queryClient]);

  return {
    start: (reason) => enqueue.mutate(reason),
    isStarting: enqueue.isPending,
    startError: enqueue.error,
    outcome,
    failure: outcome === 'failed' ? (job.data?.error ?? null) : null,
  };
}
