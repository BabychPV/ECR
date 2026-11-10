import { describe, expect, it } from 'vitest';
import { QueryClient } from '@tanstack/react-query';
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

    await refreshAfterPermitChange(client, 3);

    const stale = (key: unknown[]): boolean | undefined => client.getQueryCache().find({ queryKey: key, exact: true })?.isStale();
    expect(stale(['validation', 3, 202602])).toBe(true);
    expect(stale(['validation', 3, 202603])).toBe(true);
    expect(stale(['validation', 4, 202602])).toBe(false);
  });
});