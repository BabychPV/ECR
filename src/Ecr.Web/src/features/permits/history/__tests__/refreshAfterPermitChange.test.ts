import { describe, expect, it } from 'vitest';
import { waitFor } from '@testing-library/react';
import { QueryClient, QueryObserver } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { markSliceHidden } from '@/features/grid/sliceCache';
import { refreshAfterPermitChange } from '../usePermitHistory';

/**
 * F13 p223 (1): after a permit change (add / «Undo last» / split) the server re-runs the stored check — the page's
 * Issues query (`['validation', documentId, periodKey]`, DocumentPage) is refetched too, not only the document prefix.
 */
describe('refreshAfterPermitChange', () => {
  it('invalidates the Issues (stored validation) of the document, every period, and nothing of other documents', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
    client.setQueryData(['validation', 3, 202602], { messages: [] });
    client.setQueryData(['validation', 3, 202603], { messages: [] });
    client.setQueryData(['validation', 4, 202602], { messages: [] });

    await refreshAfterPermitChange(client, 3, 202602);

    const stale = (key: unknown[]): boolean | undefined => client.getQueryCache().find({ queryKey: key, exact: true })?.isStale();
    expect(stale(['validation', 3, 202602])).toBe(true);
    expect(stale(['validation', 3, 202603])).toBe(true);
    expect(stale(['validation', 4, 202602])).toBe(false);
  });

  /**
   * W-5 (аудит 2026-11): адресно, як `invalidateSlices` (`CL-02`/`C1-01`) — видимий змонтований зріз періоду
   * перечитується, прихований (режим «одна таблиця», `display: none`) лише стає застарілим. `slices.all()` перечитував
   * КОЖНУ змонтовану сітку (на Land — до 5,8 МБ кожна).
   */
  it('W-5: прихований змонтований зріз лише застаріває, видимий — перечитується', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
    const fetched: number[] = [];
    const observe = (tableInstanceId: number): (() => void) =>
      new QueryObserver(client, {
        queryKey: queryKeys.slices.one(tableInstanceId, 202602),
        queryFn: async () => {
          fetched.push(tableInstanceId);
          return { rows: [] };
        },
      }).subscribe(() => {});

    const stopVisible = observe(501);
    const stopHidden = observe(502);
    await waitFor(() => {
      expect([...fetched].sort()).toEqual([501, 502]);
    });
    fetched.length = 0;

    const release = markSliceHidden(502, 202602);
    try {
      await refreshAfterPermitChange(client, 3, 202602);

      await waitFor(() => {
        expect(fetched).toEqual([501]);
      });
      expect(client.getQueryState(queryKeys.slices.one(502, 202602))?.isInvalidated).toBe(true);
    } finally {
      release();
      stopVisible();
      stopHidden();
    }
  });
});