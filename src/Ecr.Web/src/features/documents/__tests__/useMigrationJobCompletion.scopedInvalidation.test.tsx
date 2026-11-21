import type { JSX, ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider, QueryObserver } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { markSliceHidden, takeMissedInvalidation } from '@/features/grid/sliceCache';
import { useMigrationJobCompletion } from '../useMigrationJobCompletion';

/**
 * PSS-19b: успіх переносу версії шаблону скидав кеш `invalidateQueries()` БЕЗ фільтра — і перезапитувалась кожна
 * змонтована сітка аркуша, зокрема приховані (до 91 найважчого `GET`). Тепер зрізи — за правилом `C1-01`:
 * видимий перезапитується, прихований лише стає застарілим і дочитується при показі; решта кешу — як і доти.
 *
 * Мутаційний доказ (не в коміті): повернути `queryClient.invalidateQueries()` → червоний (прихований зріз
 * перезапитано, `hidden` = 2).
 */

vi.mock('@/features/jobs/useJobStatus', () => ({
  useJobStatus: (jobId: string | null) =>
    jobId === null ? { data: undefined } : { data: { jobId, state: 'Succeeded', message: 'Migrated', error: null } },
}));

vi.mock('@/shared/ui/notify', () => ({ showDone: vi.fn(), showWarning: vi.fn() }));

const releases: (() => void)[] = [];

afterEach(() => {
  for (const release of releases.splice(0)) release();
});

/** Активний запит (зі спостерігачем), що рахує свої читання. */
function watched(client: QueryClient, queryKey: readonly unknown[]): { count: () => number } {
  let reads = 0;
  const observer = new QueryObserver(client, {
    queryKey,
    queryFn: () => {
      reads += 1;
      return Promise.resolve({ reads });
    },
    staleTime: Number.POSITIVE_INFINITY,
  });
  releases.push(observer.subscribe(() => undefined));

  return { count: () => reads };
}

describe('PSS-19b: завершення переносу версії — адресна інвалідація зрізів', () => {
  it('видимий зріз і решта кешу перезапитуються, прихований — лише застарілий і дочитається при показі', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const visible = watched(client, queryKeys.slices.one(1, 202609));
    const hidden = watched(client, queryKeys.slices.one(2, 202609));
    const summary = watched(client, ['document', 42, 202609]);
    releases.push(markSliceHidden(2, 202609));

    await waitFor(() => expect([visible.count(), hidden.count(), summary.count()]).toEqual([1, 1, 1]));

    const onSucceeded = vi.fn();
    const wrapper = ({ children }: { children: ReactNode }): JSX.Element => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
    renderHook(() => useMigrationJobCompletion('job-1', false, onSucceeded), { wrapper });

    await waitFor(() => expect(onSucceeded).toHaveBeenCalledTimes(1));

    expect(visible.count()).toBe(2);
    expect(summary.count()).toBe(2);
    expect(hidden.count()).toBe(1);
    expect(client.getQueryState(queryKeys.slices.one(2, 202609))?.isInvalidated).toBe(true);
    expect(takeMissedInvalidation(2, 202609)).toBe(true);
  });
});
