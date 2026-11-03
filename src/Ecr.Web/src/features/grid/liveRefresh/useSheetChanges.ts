import { useCallback, useEffect, useRef, useState } from 'react';
import { usePollWhileVisible } from '@/shared/ui/usePollWhileVisible';
import { getSheetChanges, isFinalChangesError, pollSheetChanges, SheetChangesPollMs, type SheetChanges } from './api';

/** What somebody else changed since the screen last read the sheet (accumulated over polls until a refresh). */
export interface ForeignChange {
  /** Display name of the newest author; `null` — unknown (e.g. a deleted row, a system write). */
  readonly by: string | null;
  /** UTC time of the newest change; `null` — unknown. */
  readonly at: string | null;
  /** `tableInstanceId` → `tableCode` of every visible table changed by others (slice cache keys to refetch). */
  readonly tables: ReadonlyMap<number, string>;
}

/** ✎ LIVE-STATUS: the approval state of the sheet moved since the screen last read it (any transition, anyone). */
export interface StatusChange {
  /** New state (`Draft`/`Submitted`/`Approved`/`Rejected`). */
  readonly state: string;
  /** Opaque revision of the state — a new one per transition (a step of the route keeps the state, not the revision). */
  readonly revision: string;
}

export interface SheetChangesState {
  /** `null` — nothing new from others (or the banner was dealt with). */
  readonly change: ForeignChange | null;
  /** ✎ LIVE-STATUS: the latest transition seen by the poll; `null` — none since the chain started. */
  readonly statusChange: StatusChange | null;
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
export function useSheetChanges(
  documentId: number,
  sheetDefId: number | null,
  periodKey: number,
  sheetCopyId: number | null = null,
): SheetChangesState {
  const [change, setChange] = useState<ForeignChange | null>(null);
  const [stoppedKey, setStoppedKey] = useState<string | null>(null);
  const [statusChange, setStatusChange] = useState<StatusChange | null>(null);

  // ✎ RS: a copy is its own chain (own marker, own ETag, own approval state).
  const key =
    sheetDefId === null
      ? null
      : `${String(documentId)}:${String(sheetDefId)}:${String(periodKey)}${sheetCopyId === null ? '' : `:${String(sheetCopyId)}`}`;
  const marker = useRef<string | null>(null);
  /** ✎ LIVE-STATUS: `ETag` of the last full answer — `If-None-Match` of the next poll (`304` — nothing to do). */
  const etag = useRef<string | null>(null);
  /** ✎ LIVE-STATUS: revision of the approval state the chain has seen; `null` — not yet (first answer = baseline). */
  const revision = useRef<string | null>(null);

  /** A new revision after the baseline (or `statusChanged`) is a transition; an old server without the fields — none. */
  const noteStatus = useCallback((answer: SheetChanges): void => {
    const next = typeof answer.statusRevision === 'string' ? answer.statusRevision : null;
    if (next === null) return;

    const seen = revision.current;
    revision.current = next;
    if ((seen !== null && seen !== next) || answer.statusChanged === true) {
      setStatusChange({ state: String(answer.sheetStatus), revision: next });
    }
  }, []);
  const busy = useRef(false);
  /** Bumped on every key change: answers that arrive for an older key are dropped. */
  const generation = useRef(0);

  const stopped = key !== null && stoppedKey === key;

  const poll = useCallback(async (): Promise<void> => {
    if (key === null || sheetDefId === null || busy.current) return;

    busy.current = true;
    const mine = generation.current;

    try {
      const fresh = await pollSheetChanges(documentId, sheetDefId, periodKey, marker.current, etag.current, sheetCopyId);
      if (mine !== generation.current) return;
      // ✎ LIVE-STATUS: `304` — neither the data nor the state changed for this reader; the marker stays.
      if (fresh === null) return;

      const answer = fresh.body;
      marker.current = answer.marker;
      etag.current = fresh.etag;
      noteStatus(answer);
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
  }, [documentId, sheetDefId, periodKey, sheetCopyId, key, noteStatus]);

  // A new sheet/period/document starts a new chain: take the marker at once (the baseline of "since now").
  useEffect(() => {
    generation.current += 1;
    marker.current = null;
    etag.current = null;
    revision.current = null;
    busy.current = false;
    setChange(null);
    setStatusChange(null);
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
      const answer = await getSheetChanges(documentId, sheetDefId, periodKey, marker.current, sheetCopyId);
      if (mine === generation.current) {
        marker.current = answer.marker;
        // The answer was not conditional: the next poll asks without `If-None-Match`.
        etag.current = null;
        noteStatus(answer);
      }
    } catch (error) {
      if (mine === generation.current && isFinalChangesError(error)) setStoppedKey(key);
    }
    if (mine === generation.current) setChange(null);
  }, [documentId, sheetDefId, periodKey, sheetCopyId, key, stopped, noteStatus]);

  return { change: stopped ? null : change, statusChange: stopped ? null : statusChange, stopped, clear, rebase };
}
