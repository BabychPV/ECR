import { QueryClient } from '@tanstack/react-query';
import { describe, expect, it } from 'vitest';
import { queryKeys } from '@/api/queryKeys';
import { queryRetryDelayMs } from '@/app/queryClient';
import { applySliceCachePolicy } from '../sliceCachePolicy';
import { pendingRecalculationCount, followRecalculation } from '../useCellPatch';

describe('PERF-16 client details', () => {
  it('slices are not refetched when the network comes back (a VPN flap would refetch up to 91 per user)', () => {
    const client = new QueryClient();
    applySliceCachePolicy(client);

    expect(client.getQueryDefaults(queryKeys.slices.one(1, 202609)).refetchOnReconnect).toBe(false);
  });

  it('pending recalculations are capped per document and period', () => {
    const client = new QueryClient();
    for (let i = 0; i < 500; i += 1) followRecalculation(client, `cap#${i}`, 31, 202609);

    expect(pendingRecalculationCount(31, 202609)).toBeLessThanOrEqual(64);
  });
});

describe('queryRetryDelayMs (PERF-08)', () => {
  it('doubles, is spread by +-20 %, honours Retry-After and is capped at 30 s', () => {
    expect(queryRetryDelayMs(1, new Error('x'), () => 0.5)).toBe(2_000);
    expect(queryRetryDelayMs(2, new Error('x'), () => 0.5)).toBe(4_000);
    expect(queryRetryDelayMs(1, new Error('x'), () => 0)).toBe(1_600);
    expect(queryRetryDelayMs(1, { problem: { retryAfterSeconds: 10 } }, () => 0.5)).toBe(10_000);
    expect(queryRetryDelayMs(20, new Error('x'), () => 0.5)).toBe(30_000);
  });
});
