import { QueryClient } from '@tanstack/react-query';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { queryKeys } from '@/api/queryKeys';
import { followRecalculation, isFollowedJob } from '../useCellPatch';

/**
 * PERF-08: a job that waits in the queue for a long time must neither hammer the server (2 s beat) nor be
 * forgotten after 2-10 minutes.
 */
const slice = queryKeys.slices.one(900, 202609);

function setup(stateOf: () => string): { client: QueryClient; polls: () => number } {
  let polls = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => {
      polls += 1;

      return new Response(JSON.stringify({ state: stateOf() }), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
  const client = new QueryClient({ defaultOptions: { queries: { gcTime: Infinity } } });
  client.setQueryData(slice, { rows: [] });

  return { client, polls: () => polls };
}

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('followRecalculation while the job is queued', () => {
  it('backs off: 10 minutes of queue cost ~45 asks, not the 300 of a 2 s beat; still following after 15 minutes', async () => {
    vi.useFakeTimers();
    const { client, polls } = setup(() => 'Queued');
    followRecalculation(client, 'queued#1', 21, 202609);

    await vi.advanceTimersByTimeAsync(10 * 60_000);
    expect(polls()).toBeLessThan(70);
    expect(polls()).toBeGreaterThan(20);

    await vi.advanceTimersByTimeAsync(5 * 60_000);
    expect(isFollowedJob('queued#1')).toBe(true);
  });

  it('a long wait that ends is still picked up: the job starts, finishes, and the slice is refetched', async () => {
    vi.useFakeTimers();
    let state = 'Queued';
    const { client } = setup(() => state);
    followRecalculation(client, 'queued#2', 22, 202609);

    await vi.advanceTimersByTimeAsync(25 * 60_000);
    expect(client.getQueryState(slice)?.isInvalidated).not.toBe(true);

    state = 'Succeeded';
    await vi.advanceTimersByTimeAsync(20_000);
    expect(isFollowedJob('queued#2')).toBe(false);
    expect(client.getQueryState(slice)?.isInvalidated).toBe(true);
  });

  it('giving up after the queue ceiling marks the slices stale (no silent loss without a mounted grid)', async () => {
    vi.useFakeTimers();
    const { client } = setup(() => 'Queued');
    followRecalculation(client, 'queued#3', 23, 202609);

    await vi.advanceTimersByTimeAsync(62 * 60_000);
    expect(isFollowedJob('queued#3')).toBe(false);
    expect(client.getQueryState(slice)?.isInvalidated).toBe(true);
  });
});
