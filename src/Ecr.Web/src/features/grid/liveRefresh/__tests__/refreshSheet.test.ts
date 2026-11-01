import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { QueryClient, QueryObserver } from '@tanstack/react-query';
import { beginInFlight, resetInFlight } from '../../inFlightEdits';
import { openDocument, pendingSlice, putPendingEdit, resetPending } from '../../pendingStore';
import type { PendingEdit } from '../../cellPatchRequest';
import { markSliceHidden, takeMissedInvalidation } from '../../sliceCache';
import { refreshSheetTables } from '../refreshSheet';

/**
 * LIVE-REFRESH B3: "Refresh" re-reads the named slices through the grid's own cache keys and KEEPS the user's edits:
 * it waits for own PATCHes in flight, does not touch the pending store, holds the autosave during the refetch and
 * plans it again after.
 *
 * Mutation proofs: drop `await whenInFlightSettles()` in `refreshSheet.ts` → the
 * first test is red (the slice is refetched while the PATCH flies); drop the `scheduleAutosave()` in `finally` →
 * the autosave test is red.
 */
const autosave = vi.hoisted(() => ({ cancel: vi.fn(), schedule: vi.fn() }));
vi.mock('../../autosave', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../autosave')>()),
  cancelAutosave: autosave.cancel,
  scheduleAutosave: autosave.schedule,
}));

const Period = 202610;

function edit(rowKey: string, value: string): PendingEdit {
  return { rowKey, columnCode: 'C1', value, isEmpty: false, baseVersion: 'v1', before: null };
}

interface Watched {
  readonly fetched: () => number;
  readonly stop: () => void;
}

/** A mounted grid of one slice: an active observer whose fetch count we read. */
async function watch(client: QueryClient, tableInstanceId: number): Promise<Watched> {
  let count = 0;
  const observer = new QueryObserver(client, {
    queryKey: ['table-slice', tableInstanceId, Period],
    queryFn: () => {
      count += 1;
      return Promise.resolve({ tableInstanceId, rows: [], fetched: count });
    },
    structuralSharing: false,
    staleTime: 5 * 60_000,
  });
  const stop = observer.subscribe(() => undefined);
  await vi.waitFor(() => expect(count).toBe(1));

  return { fetched: () => count, stop };
}

const sleep = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

let client: QueryClient;

beforeEach(() => {
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  resetPending();
  resetInFlight();
  openDocument(1);
  autosave.cancel.mockClear();
  autosave.schedule.mockClear();
});

afterEach(() => {
  client.clear();
  resetPending();
  resetInFlight();
});

describe('refreshSheetTables', () => {
  it('waits for own PATCHes in flight, then refetches; the unsaved edit stays in the pending store', async () => {
    const grid = await watch(client, 11);
    putPendingEdit(11, Period, edit('R2', 'mine, not saved yet'));
    const release = beginInFlight(11, Period, [edit('R1', 'flying')]);

    const done = refreshSheetTables(client, Period, [11]);
    await sleep(250);
    // ⛔ Still flying: a refetch now could read the row BEFORE the PATCH and land after its answer.
    expect(grid.fetched()).toBe(1);

    release();
    await done;
    expect(grid.fetched()).toBe(2);
    expect([...pendingSlice(11, Period).values()].map((value) => value.value)).toEqual(['mine, not saved yet']);
    grid.stop();
  });

  it('refetches only the named tables; a hidden grid is not refetched but is read when shown', async () => {
    const named = await watch(client, 11);
    const other = await watch(client, 12);
    const hidden = await watch(client, 13);
    const release = markSliceHidden(13, Period);

    await refreshSheetTables(client, Period, [11, 13]);

    expect(named.fetched()).toBe(2);
    expect(other.fetched()).toBe(1);
    expect(hidden.fetched()).toBe(1);
    expect(client.getQueryState(['table-slice', 13, Period])?.isInvalidated).toBe(true);
    expect(takeMissedInvalidation(13, Period)).toBe(true);
    expect(client.getQueryState(['table-slice', 12, Period])?.isInvalidated).toBe(false);

    release();
    [named, other, hidden].forEach((grid) => grid.stop());
  });

  it('holds the autosave during the refetch and plans it again when there is something to send', async () => {
    const grid = await watch(client, 11);
    putPendingEdit(11, Period, edit('R2', 'typed'));

    await refreshSheetTables(client, Period, [11]);

    expect(autosave.cancel).toHaveBeenCalledTimes(1);
    expect(autosave.schedule).toHaveBeenCalledTimes(1);
    expect(autosave.cancel.mock.invocationCallOrder[0]).toBeLessThan(autosave.schedule.mock.invocationCallOrder[0] ?? 0);
    grid.stop();
  });

  it('nothing pending — the autosave is not planned for nothing', async () => {
    const grid = await watch(client, 11);

    await refreshSheetTables(client, Period, [11]);

    expect(autosave.schedule).not.toHaveBeenCalled();
    grid.stop();
  });
});
