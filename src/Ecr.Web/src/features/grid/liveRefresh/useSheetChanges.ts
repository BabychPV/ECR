import { useCallback, useEffect, useRef, useState } from 'react';
import { usePollWhileVisible } from '@/shared/ui/usePollWhileVisible';
import { getSheetChanges, isFinalChangesError, SheetChangesPollMs } from './api';

/** What somebody else changed since the screen last read the sheet (accumulated over polls until a refresh). */
export interface ForeignChange {
  /** Display name of the newest author; `null` — unknown (e.g. a deleted row, a system write). */
  readonly by: string | null;
  /** UTC time of the newest change; `null` — unknown. */
  readonly at: string | null;
  /** `tableInstanceId` → `tableCode` of every visible table changed by others (slice cache keys to refetch). */
  readonly tables: ReadonlyMap<number, string>;
}

export interface SheetChangesState {
  /** `null` — nothing new from others (or the banner was dealt with). */
  readonly change: ForeignChange | null;
  /** The server said the poll cannot succeed for this sheet/period (`404`/`422`): no more requests. */
  readonly stopped: boolean;
  /** The change was taken care of (refresh done): hide the banner until the next foreign change. */
  readonly clear: () => void;
  /** Move the marker to "now" and hide the banner (before a refresh of the whole sheet). */
  readonly rebase: () => Promise<void>;
}

/**
 * LIVE-REFRESH B2: polls the change marker of the sheet every 30 s while the tab is visible.
 *
 * Rules (contract of `GET …/sheets/{sheetDefId}/changes`):
 *  - the first call has no `since` and only takes the marker; every answer's marker is stored and sent next time;
 *  - own PATCHes never answer `changed = true` (the server filters them) — the banner is about OTHER people only;
 *  - `404` (hidden sheet / document) and `422` (bad period) stop the poll for this key: no banner, no retry storm;
 *  - any other failure is silent: the next tick asks again with the same marker.
 *
 * ⛔ Not `useQuery`: each request depends on the previous answer (marker chain) and the answer is an EVENT to
 * accumulate, not data to cache — a cached `changed: true` would bring the banner back after it was dismissed.
 */
export function useSheetChanges(documentId: number, sheetDefId: number | null, periodKey: number): SheetChangesState {
  const [change, setChange] = useState<ForeignChange | null>(null);
  const [stoppedKey, setStoppedKey] = useState<string | null>(null);

  const key = sheetDefId === null ? null : `${String(documentId)}:${String(sheetDefId)}:${String(periodKey)}`;
  const marker = useRef<string | null>(null);
  const busy = useRef(false);
  /** Bumped on every key change: answers that arrive for an older key are dropped. */
  const generation = useRef(0);

  const stopped = key !== null && stoppedKey === key;

  const poll = useCallback(async (): Promise<void> => {
    if (key === null || sheetDefId === null || busy.current) return;

    busy.current = true;
    const mine = generation.current;

    try {
      const answer = await getSheetChanges(documentId, sheetDefId, periodKey, marker.current);
      if (mine !== generation.current) return;

      marker.current = answer.marker;
      if (!answer.changed) return;

      setChange((previous) => {
        const tables = new Map(previous?.tables ?? []);
        for (const table of answer.changedTables) tables.set(table.tableInstanceId, table.tableCode);

        return { by: answer.lastChangedBy, at: answer.lastChangedAt, tables };
      });
    } catch (error) {
      if (mine !== generation.current) return;
      // ⚠ Errors are not shown: the marker is an accessory of the sheet; the grid itself reports its own reads.
      if (isFinalChangesError(error)) setStoppedKey(key);
    } finally {
      busy.current = false;
    }
  }, [documentId, sheetDefId, periodKey, key]);

  // A new sheet/period/document starts a new chain: take the marker at once (the baseline of "since now").
  useEffect(() => {
    generation.current += 1;
    marker.current = null;
    busy.current = false;
    setChange(null);
    if (key !== null) void poll();

    return () => {
      generation.current += 1;
    };
  }, [key]);

  usePollWhileVisible(poll, key === null || stopped ? null : SheetChangesPollMs);

  const clear = useCallback((): void => setChange(null), []);

  /*
   * Before a refresh: move the marker to "now" so what the refetch is about to read does not come back as a banner
   * at the next tick. ⚠ Taken BEFORE the refetch: a change in between shows a banner for something already on the
   * screen (harmless), never the other way round (a change lost without a banner).
   */
  const rebase = useCallback(async (): Promise<void> => {
    if (key === null || sheetDefId === null || stopped) return;

    // A poll in flight answers about the time BEFORE the refresh: its "changed" would bring the banner back.
    generation.current += 1;
    const mine = generation.current;
    try {
      const answer = await getSheetChanges(documentId, sheetDefId, periodKey, marker.current);
      if (mine === generation.current) marker.current = answer.marker;
    } catch (error) {
      if (mine === generation.current && isFinalChangesError(error)) setStoppedKey(key);
    }
    if (mine === generation.current) setChange(null);
  }, [documentId, sheetDefId, periodKey, key, stopped]);

  return { change: stopped ? null : change, stopped, clear, rebase };
}
