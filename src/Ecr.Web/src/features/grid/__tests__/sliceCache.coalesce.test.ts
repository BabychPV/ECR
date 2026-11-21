import { QueryClient, QueryObserver } from '@tanstack/react-query';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { queryKeys } from '@/api/queryKeys';
import { invalidateSlices, invalidateSlicesForPeriods } from '../sliceCache';

/**
 * PERF-09: a burst of invalidations (job done + someone else's status change + import) must not cancel and
 * restart the slice request that is already in flight, once per event; it costs at most two requests per slice.
 */
const Period = 202609;

interface Stand {
  client: QueryClient;
  reads: () => number;
  /** Resolves the oldest unresolved request of the slice. */
  finish: () => void;
  stop: () => void;
}

function stand(): Stand {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } });
  client.mount();
  let reads = 0;
  const gates: (() => void)[] = [];
  const observer = new QueryObserver(client, {
    queryKey: queryKeys.slices.one(700, Period),
    queryFn: () => {
      reads += 1;

      return new Promise<{ n: number }>((resolve) => gates.push(() => resolve({ n: reads })));
    },
  });
  const unsubscribe = observer.subscribe(() => undefined);

  return {
    client,
    reads: () => reads,
    finish: () => gates.shift()?.(),
    stop: () => {
      unsubscribe();
      client.unmount();
    },
  };
}

const setVisibility = (state: 'visible' | 'hidden'): void => {
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
  document.dispatchEvent(new Event('visibilitychange'));
};

afterEach(() => {
  setVisibility('visible');
  vi.restoreAllMocks();
});

describe('invalidation burst (PERF-09)', () => {
  it('five events while a refetch is in flight cost 2 slice reads, not 6', async () => {
    const s = stand();
    s.finish(); // first load
    await vi.waitFor(() => expect(s.reads()).toBe(1));
    await Promise.resolve();

    const first = invalidateSlicesForPeriods(s.client, 7, [Period]);
    await vi.waitFor(() => expect(s.reads()).toBe(2));
    const rest = [
      invalidateSlices(s.client, { documentId: 7, periodKey: Period }),
      invalidateSlices(s.client, { documentId: 7, periodKey: Period, sheetDefId: 1 }),
      invalidateSlicesForPeriods(s.client, 7, [Period]),
      invalidateSlices(s.client, { documentId: 7, periodKey: Period }),
    ];
    // Nothing was cancelled or restarted while the request flies.
    expect(s.reads()).toBe(2);

    s.finish();
    await first;
    // ONE merged trailing pass for the four later events.
    await vi.waitFor(() => expect(s.reads()).toBe(3));
    s.finish();
    await Promise.all(rest);
    expect(s.reads()).toBe(3);
    s.stop();
  });

  it('the pass that follows a burst starts AFTER the last event (no answer from before it is kept)', async () => {
    const s = stand();
    s.finish();
    await vi.waitFor(() => expect(s.reads()).toBe(1));
    await Promise.resolve();

    const first = invalidateSlices(s.client, { documentId: 7, periodKey: Period });
    await vi.waitFor(() => expect(s.reads()).toBe(2));
    const late = invalidateSlices(s.client, { documentId: 7, periodKey: Period });
    s.finish();
    await first;
    await vi.waitFor(() => expect(s.reads()).toBe(3));
    s.finish();
    await late;
    expect(s.client.getQueryState(queryKeys.slices.one(700, Period))?.data).toEqual({ n: 3 });
    s.stop();
  });

  it('a hidden tab refetches nothing; coming back reads the slice once', async () => {
    const s = stand();
    s.finish();
    await vi.waitFor(() => expect(s.reads()).toBe(1));
    await Promise.resolve();

    setVisibility('hidden');
    await invalidateSlicesForPeriods(s.client, 7, [Period]);
    await invalidateSlices(s.client, { documentId: 7, periodKey: Period });
    expect(s.reads()).toBe(1);
    expect(s.client.getQueryState(queryKeys.slices.one(700, Period))?.isInvalidated).toBe(true);

    setVisibility('visible');
    await vi.waitFor(() => expect(s.reads()).toBe(2));
    s.finish();
    await Promise.resolve();
    expect(s.reads()).toBe(2);
    s.stop();
  });
});
