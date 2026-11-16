import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { queryKeys } from '@/api/queryKeys';
import { rowLocksKey } from '@/features/documents/rowLocks/api';
import { useCreateRowLocks, useReleaseRowLock } from '../useRowLocks';

/**
 * ✎ B139 (F13, пункт a): після створення чи зняття блокування застарілими стають І перелік блокувань документа
 * (`rowLocksKey`), І зрізи таблиць документа за період — `cellPermissions` (причина `RowLocked`) рахує сервер, тож
 * без нового зрізу комірки заблокованого рядка лишалися б «правними» до перезавантаження.
 *
 * ⛔ Мутації (`refreshAfterChange` у `useRowLocks.ts`): прибрати `client.invalidateQueries({ queryKey: rowLocksKey… })`
 * — червоніє перевірка переліку; прибрати `invalidateSlices(…)` — червоніє перевірка зрізу. Обидві — в обох тестах.
 */
const DocumentId = 96;
const PeriodKey = 202611;
const TableInstanceId = 501;

const LocksKey = rowLocksKey(DocumentId, PeriodKey);
const SliceKey = queryKeys.slices.one(TableInstanceId, PeriodKey);

function seededClient(): QueryClient {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(LocksKey, []);
  // Перелік таблиць документа — за ним `invalidateSlices` вирішує, які зрізи в межах наміру.
  client.setQueryData(['document-tables', DocumentId, PeriodKey], [{ tableInstanceId: TableInstanceId, sheetDefId: 1 }]);
  client.setQueryData(SliceKey, { rows: [], columns: [] });

  return client;
}

function stubServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) =>
      init?.method === 'DELETE'
        ? new Response(null, { status: 204 })
        : new Response(JSON.stringify([]), { status: 201, headers: { 'Content-Type': 'application/json' } }),
    ),
  );
}

const invalidated = (client: QueryClient, key: readonly unknown[]): boolean | undefined =>
  client.getQueryState(key)?.isInvalidated;

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('useRowLocks: оновлення після зміни блокувань (B139, F13 a)', () => {
  it('створення: перелік блокувань і зріз таблиці документа стають застарілими (до успіху — ні)', async () => {
    stubServer();
    const client = seededClient();
    const created = vi.fn();
    const { result } = renderHook(() => useCreateRowLocks(DocumentId, PeriodKey, created), {
      wrapper: ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>,
    });

    expect(invalidated(client, LocksKey)).toBe(false);
    expect(invalidated(client, SliceKey)).toBe(false);

    await act(async () => {
      await result.current.mutateAsync({
        periodKey: PeriodKey,
        sheetCode: 'S1',
        tableCode: 'T1',
        sheetCopyId: null,
        rowKeys: ['R1'],
        fromDate: '2026-11-10',
        toDate: '2026-11-20',
        note: null,
      });
    });

    expect(created).toHaveBeenCalledTimes(1);
    expect(invalidated(client, LocksKey), 'перелік блокувань').toBe(true);
    expect(invalidated(client, SliceKey), 'зріз таблиці').toBe(true);
  });

  it('зняття: перелік блокувань і зріз таблиці документа стають застарілими (до успіху — ні)', async () => {
    stubServer();
    const client = seededClient();
    const released = vi.fn();
    const { result } = renderHook(() => useReleaseRowLock(DocumentId, PeriodKey, released), {
      wrapper: ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>,
    });

    expect(invalidated(client, LocksKey)).toBe(false);
    expect(invalidated(client, SliceKey)).toBe(false);

    await act(async () => {
      await result.current.mutateAsync(5);
    });

    expect(released).toHaveBeenCalledTimes(1);
    expect(invalidated(client, LocksKey), 'перелік блокувань').toBe(true);
    expect(invalidated(client, SliceKey), 'зріз таблиці').toBe(true);
  });
});
