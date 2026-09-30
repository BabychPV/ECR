import { useEffect, useRef } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { JobStatus } from '@/api/types';
import { outcomeOf, pollInterval, type JobOutcome } from '@/features/workflow/jobFollow';
import { recalculateImpacted, registryImpact } from './api';

/** Право на постановку перерахунку (`RecalculateImpactedHandler.Permission`). */
export const RecalculateImpactedPermission = 'Calculation.Recalculate';

/** Ключ переліку зачеплених документів довідника. */
export function impactKey(code: string): readonly unknown[] {
  return ['registries', 'impact', code];
}

/** Перелік документів відкритих періодів, зачеплених правкою довідника (лише читання). */
export function useRegistryImpact(code: string) {
  return useQuery({
    queryKey: impactKey(code),
    queryFn: () => registryImpact(code),
  });
}

/** Стан перерахунку зачеплених: постановка, стеження за задачею (патерн `useConsistencyRun`). */
export interface ImpactRecalculation {
  start: (reason: string) => void;
  isStarting: boolean;
  /** Відмова ПОСТАНОВКИ (403/422 із каталогу помилок); `null` — не було. */
  startError: unknown;
  outcome: JobOutcome | null;
  failure: string | null;
}

/**
 * Перерахунок зачеплених документів і стеження за задачею.
 *
 * ⛔ Правило опитування й підсумку — спільне (`jobFollow.ts`); перелік перечитується один
 * раз на кінцевому `Succeeded`, а не на `202` (у момент постановки нічого не змінилося).
 */
export function useImpactRecalculation(code: string): ImpactRecalculation {
  const queryClient = useQueryClient();
  const enqueue = useMutation({
    mutationFn: (reason: string) => recalculateImpacted(code, { documentIds: null, reason }),
  });
  const jobId = enqueue.data?.jobId ?? null;

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
    void queryClient.invalidateQueries({ queryKey: impactKey(code) });
  }, [jobId, outcome, code, queryClient]);

  return {
    start: (reason) => enqueue.mutate(reason),
    isStarting: enqueue.isPending,
    startError: enqueue.error,
    outcome,
    failure: outcome === 'failed' ? (job.data?.error ?? null) : null,
  };
}
