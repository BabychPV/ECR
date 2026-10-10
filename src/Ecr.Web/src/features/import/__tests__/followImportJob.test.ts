import { afterEach, describe, expect, it, vi } from 'vitest';
import { QueryClient } from '@tanstack/react-query';
import { followImportJob } from '../followImportJob';

/**
 * `G1-07`: фоновий імпорт (`202` + `jobId`) після кінцевого стану задачі
 * перечитує зрізи документа/періоду. Доти гілка `202` інвалідувала лише
 * `['jobs']`, і сітка показувала дані до імпорту.
 *
 * ⚠ Детерміновано: очікування між опитуваннями підмінено (`wait`), стани задачі
 * віддає заглушка `fetch` у заданому порядку.
 */

function jobResponses(states: string[]): void {
  const queue = [...states];
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = String(input);
      if (!url.includes('/api/v1/jobs/')) throw new Error(`неочікуваний запит: ${url}`);
      const state = queue.length > 1 ? queue.shift() : queue[0];

      return Promise.resolve(
        new Response(JSON.stringify({ id: 'job-1', state }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('followImportJob (G1-07)', () => {
  it('Running → Succeeded: зріз документа/періоду перечитано рівно після завершення', async () => {
    jobResponses(['Running', 'Running', 'Succeeded']);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    // Зріз таблиці цього документа в кеші (перелік таблиць — з `document-tables`).
    client.setQueryData(['document-tables', 1, 202609], [{ tableInstanceId: 70, sheetDefId: 1 }]);
    client.setQueryData(['table-slice', 70, 202609], { rows: [] });
    client.setQueryData(['table-slice', 71, 202610], { rows: [] });

    const waits: number[] = [];

    const outcome = await followImportJob(client, 'job-1', 1, 202609, {
      pollMs: 5,
      wait: (ms) => {
        waits.push(ms);
        // Поки задача йде, зріз НЕ інвалідовано.
        expect(client.getQueryState(['table-slice', 70, 202609])?.isInvalidated).toBe(false);
        return Promise.resolve();
      },
    });

    expect(outcome).toBe('succeeded');
    expect(waits).toEqual([5, 5]);
    expect(client.getQueryState(['table-slice', 70, 202609])?.isInvalidated).toBe(true);
  });

  it('стан задачі прочитати не вдалося — зрізи однаково позначено застарілими', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(new Response(null, { status: 403 }))),
    );
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    client.setQueryData(['table-slice', 70, 202609], { rows: [] });

    const outcome = await followImportJob(client, 'job-1', 1, 202609, { wait: () => Promise.resolve() });

    expect(outcome).toBe('unknown');
    expect(client.getQueryState(['table-slice', 70, 202609])?.isInvalidated).toBe(true);
  });
});
