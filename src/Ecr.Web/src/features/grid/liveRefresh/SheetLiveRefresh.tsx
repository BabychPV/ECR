import { useCallback, useEffect, useRef, useState, type JSX } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import type { DocumentSummary, DocumentTableDto } from '@/api/types';
import { sheetStateKey } from '@/features/documents/sheetCopies/sheetCopyTypes';
import { workflowHistoryQuery } from '@/features/workflow/api';
import { refreshSheetWorkflow } from '@/features/workflow/refreshSheetWorkflow';
import { whileStatusStale } from '@/features/workflow/statusStale';
import { formatTime } from '@/shared/format/datetime';
import { t } from '@/shared/i18n';
import { isRefreshKey, isTypingOrDialogTarget } from '../shortcutKey';
import { refreshSheetTables } from './refreshSheet';
import { useSheetChanges, type ForeignChange, type StatusChange } from './useSheetChanges';
import './liveRefresh.css';

export interface SheetLiveRefreshProps {
  readonly documentId: number;
  readonly periodKey: number;
  /** Tables of the active sheet the user sees (`GET …/documents/{id}/tables`, one sheet). */
  readonly tables: readonly DocumentTableDto[];
}

export interface SheetLiveRefresh {
  /** Toolbar "Refresh" (Shift+F5): re-read every table of the sheet, keeping the user's edits. */
  readonly button: JSX.Element;
  /** Polite live region with "Updated by <user> at HH:MM — Refresh" when somebody else changed the sheet. */
  readonly banner: JSX.Element;
}

/**
 * LIVE-REFRESH B2/B3 (`.sync-local/plans/LIVE-REFRESH-PLAN.md`): data of other users on the open sheet.
 *
 * ⛔ No silent auto-reload under the user's hands: the poll only SAYS that the sheet changed; the refetch happens on
 * a click (banner or toolbar) or Shift+F5. Own edits never raise the banner (the server filters them).
 * ⚠ Lives in the grid chunk (`SheetWorkspace`), so the route budget (`D-132`) does not grow.
 */
export function useSheetLiveRefresh({ documentId, periodKey, tables }: SheetLiveRefreshProps): SheetLiveRefresh {
  const client = useQueryClient();
  const sheetDefId = tables[0]?.sheetDefId ?? null;
  const sheetCopyId = tables[0]?.sheetCopyId ?? null;
  const changes = useSheetChanges(documentId, sheetDefId, periodKey, sheetCopyId);
  const [busy, setBusy] = useState(false);
  const inFlight = useRef(false);

  const tableIds = tables.map((table) => table.tableInstanceId);
  const idsRef = useRef(tableIds);
  idsRef.current = tableIds;
  const changesRef = useRef(changes);
  changesRef.current = changes;

  const run = useCallback(
    async (only: Iterable<number> | null): Promise<void> => {
      if (inFlight.current) return;
      inFlight.current = true;
      setBusy(true);
      try {
        if (only === null) {
          // The whole sheet: whatever the banner announced is read too.
          await changesRef.current.rebase();
          await refreshSheetTables(client, periodKey, idsRef.current);
        } else {
          changesRef.current.clear();
          await refreshSheetTables(client, periodKey, only);
        }
      } finally {
        inFlight.current = false;
        setBusy(false);
      }
    },
    [client, periodKey],
  );

  // Shift+F5 — the same action as the button; not from an input, a cell editor or a dialog.
  const runRef = useRef(run);
  runRef.current = run;
  useEffect(() => {
    const onKey = (event: KeyboardEvent): void => {
      if (!isRefreshKey(event) || isTypingOrDialogTarget(event.target)) return;
      event.preventDefault();
      void runRef.current(null);
    };

    window.addEventListener('keydown', onKey);

    return () => window.removeEventListener('keydown', onKey);
  }, []);

  const label = t('liveRefresh.refresh');
  const button = (
    <button
      type="button"
      className="ecr-full-btn ecr-live-refresh-btn"
      title={t('liveRefresh.refreshTitle')}
      aria-keyshortcuts="Shift+F5"
      aria-busy={busy}
      onClick={() => void run(null)}
      data-testid="sheet-refresh"
    >
      <RefreshGlyph spinning={busy} />
      <span>{label}</span>
    </button>
  );

  const status = useStatusTransition(
    documentId,
    periodKey,
    sheetDefId,
    tables[0]?.sheetCode ?? null,
    sheetCopyId,
    changes.statusChange,
  );

  const change = changes.change;
  const banner = (
    <div className="ecr-live-host" role="status" aria-live="polite" aria-atomic="true" data-testid="sheet-live-host">
      {status.notice !== null && (
        <div className="ecr-live-banner" data-testid="sheet-status-banner" data-live-status={status.notice.state}>
          <span className="ecr-live-banner-text">{statusBannerText(status.notice)}</span>
          <button
            type="button"
            className="ecr-full-btn ecr-live-refresh-btn"
            onClick={status.dismiss}
            data-testid="sheet-status-dismiss"
          >
            {t('common.close')}
          </button>
        </div>
      )}
      {change !== null && (
        <div className="ecr-live-banner" data-testid="sheet-live-banner" data-live-tables={[...change.tables.keys()].join(',')}>
          <span className="ecr-live-banner-text">{bannerText(change)}</span>
          <button
            type="button"
            className="ecr-full-btn ecr-live-refresh-btn"
            aria-busy={busy}
            onClick={() => void run(change.tables.keys())}
            data-testid="sheet-live-refresh"
          >
            {label}
          </button>
        </div>
      )}
    </div>
  );

  return { button, banner };
}

/** "Updated by <user> at HH:MM"; unknown author or time — the generic text (never a made-up name). */
export function bannerText(change: ForeignChange): string {
  const time = change.at === null ? '' : formatTime(change.at);

  if (time === '') return t('liveRefresh.updatedNoTime');
  if (change.by === null || change.by.trim() === '') return t('liveRefresh.updatedByOther', { time });

  return t('liveRefresh.updatedBy', { user: change.by, time });
}

/** ✎ LIVE-STATUS: what the "Status changed" banner says. */
export interface StatusNotice {
  readonly state: string;
  /** Author of the transition from the transition log; `null` — unknown (never a made-up name). */
  readonly by: string | null;
}

/** "Status changed by <user>: Submitted"; unknown author — "by another user". */
export function statusBannerText(notice: StatusNotice): string {
  const status = sheetStateLabel(notice.state);
  if (notice.by === null || notice.by.trim() === '') return t('liveStatus.changedByOther', { status });

  return t('liveStatus.changedBy', { user: notice.by, status });
}

/** Label of a sheet state (`status.sheet.*`, literal keys for the catalog guard); an unknown state — as is. */
function sheetStateLabel(state: string): string {
  switch (state) {
    case 'Draft':
      return t('status.sheet.Draft');
    case 'Submitted':
      return t('status.sheet.Submitted');
    case 'Approved':
      return t('status.sheet.Approved');
    case 'Rejected':
      return t('status.sheet.Rejected');
    default:
      return state;
  }
}

/**
 * ✎ LIVE-STATUS: a transition of the sheet seen by the poll → re-read everything that depends on the state
 * (`refreshSheetWorkflow` — the same keys as own workflow actions) while the action bar is held disabled
 * (`whileStatusStale`), then a polite notice — only when the state on the screen really moved (own action in this
 * tab already re-read it; a route step keeps the state: buttons refresh silently).
 */
function useStatusTransition(
  documentId: number,
  periodKey: number,
  sheetDefId: number | null,
  sheetCode: string | null,
  sheetCopyId: number | null,
  transition: StatusChange | null,
): { readonly notice: StatusNotice | null; readonly dismiss: () => void } {
  const client = useQueryClient();
  const [notice, setNotice] = useState<StatusNotice | null>(null);
  const handled = useRef<string | null>(null);

  useEffect(() => {
    setNotice(null);
  }, [documentId, periodKey, sheetDefId]);

  useEffect(() => {
    if (transition === null || sheetDefId === null || handled.current === transition.revision) return;
    handled.current = transition.revision;

    // The state the page shows now (`DocumentPage`: a sheet without a row is `Draft`); no summary — unknown.
    const summary = client.getQueryData<DocumentSummary>(queryKeys.document.summary(documentId, periodKey));
    // ✎ RS: a copy has its own key in `sheetStates` (`CODE#sheetCopyId`).
    const shown =
      sheetCode === null || summary === undefined
        ? undefined
        : (summary.sheetStates[sheetStateKey(sheetCode, sheetCopyId)] ?? 'Draft');
    const moved = shown !== undefined && shown !== transition.state;

    void whileStatusStale(documentId, periodKey, () =>
      refreshSheetWorkflow(client, documentId, { sheetDefId, periodKey }),
    ).then(async () => {
      if (!moved || handled.current !== transition.revision) return;

      let by: string | null = null;
      try {
        const events = await client.fetchQuery({ ...workflowHistoryQuery(documentId, periodKey), staleTime: 5_000 });
        const latest = Array.isArray(events)
          ? events.find((event) => event.sheetCode === sheetCode && event.toState === transition.state)
          : undefined;
        by = latest?.byDisplayName ?? null;
      } catch {
        // The author is an accessory: without the log the notice says "another user".
      }
      if (handled.current === transition.revision) setNotice({ state: transition.state, by });
    });
  }, [client, documentId, periodKey, sheetDefId, sheetCode, sheetCopyId, transition]);

  return { notice, dismiss: useCallback(() => setNotice(null), []) };
}

/** Two arrows in a circle; decorative — the button has its text. */
function RefreshGlyph({ spinning }: { readonly spinning: boolean }): JSX.Element {
  return (
    <svg
      width={14}
      height={14}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.75}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
      data-spin={spinning ? '' : undefined}
    >
      <path d="M20 11a8 8 0 0 0-14.3-4.9L4 8M4 4v4h4M4 13a8 8 0 0 0 14.3 4.9L20 16M20 20v-4h-4" />
    </svg>
  );
}
