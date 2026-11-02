import type { QueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { invalidateSlices } from '@/features/grid/sliceCache';

/** Sheet × period whose approval state changed. */
export interface SheetAddress {
  readonly sheetDefId: number;
  readonly periodKey: number;
}

/**
 * Re-reads everything that depends on the approval state of a sheet × period — ONE list for both callers:
 * own workflow actions (`useSheetActions`) and a transition by someone else seen by the live poll (LIVE-STATUS).
 *
 * - `['document', id, period]` — summary (sheet states → banner, lock, buttons), workflow history, recall and
 *   approver-edit availability (all under this prefix);
 * - ⛔ AN-28/L8-02: slices of THIS sheet — `cellPermissions` depend on the state (`EditRules.CanEdit` →
 *   `DocumentSubmitted`); other sheets' slices are only marked stale;
 * - ✎ D-441: submission target and month-column statuses depend on the states of the sheet in ALL periods;
 * - ✎ D-449: "Awaiting approval" / "Returned" in My tasks — not awaited (not this page).
 */
export async function refreshSheetWorkflow(client: QueryClient, documentId: number, at: SheetAddress): Promise<void> {
  await client.invalidateQueries({ queryKey: queryKeys.document.summary(documentId, at.periodKey) });
  await invalidateSlices(client, { documentId, periodKey: at.periodKey, sheetDefId: at.sheetDefId });
  await client.invalidateQueries({ queryKey: ['submission-target', documentId] });
  void client.invalidateQueries({ queryKey: ['approval-queue'] });
  void client.invalidateQueries({ queryKey: ['returned-sheets'] });
}
