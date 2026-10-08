import { QueryClient } from '@tanstack/react-query';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { queryKeys } from '@/api/queryKeys';
import { followRecalculation, settleRecalculation } from '../useCellPatch';

/**
 * N-1 (RC15): після правки на одному аркуші зрізи ІНШИХ таблиць документа (Rollup на «Contract») не
 * лишаються зі значеннями до перерахунку, навіть коли сітка, що поставила задачу, вже розмонтована.
 */
const other = queryKeys.slices.one(900, 202609);
const foreignPeriod = queryKeys.slices.one(901, 202610);

function clientWithSlices(): QueryClient {
  const client = new QueryClient();
  client.setQueryData(other, { rows: [] });
  client.setQueryData(foreignPeriod, { rows: [] });
  return client;
}

const stale = (client: QueryClient, key: readonly unknown[]): boolean =>
  client.getQueryState(key)?.isInvalidated === true;

afterEach(() => vi.unstubAllGlobals());

describe('перерахунок скидає зрізи документа', () => {
  it('settleRecalculation: зріз іншого аркуша застарів, інший період - ні; повтор задачі ігнорується', async () => {
    const client = clientWithSlices();
    expect(settleRecalculation(client, 'job#1', 7, 202609)).toBe(true);
    await vi.waitFor(() => expect(stale(client, other)).toBe(true));
    expect(stale(client, foreignPeriod)).toBe(false);
    expect(settleRecalculation(client, 'job#1', 7, 202609)).toBe(false);
  });

  it('followRecalculation: задача завершилась без жодної змонтованої сітки - зрізи застарілі', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        new Response(JSON.stringify({ state: 'Succeeded' }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })),
    );
    const client = clientWithSlices();
    followRecalculation(client, 'job#2', 7, 202609);
    await vi.waitFor(() => expect(stale(client, other)).toBe(true));
  });
});
