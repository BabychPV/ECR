import { apiFetch, EcrApiError } from '@/api/client';
import type { components } from '@/api/schema';

/** LIVE-REFRESH B1: answer of the change marker of a sheet (`SheetChangesController`). */
export type SheetChanges = components['schemas']['SheetChangesDto'];

/** Poll interval of the change marker while the tab is visible (LIVE-REFRESH B2: 30 s). */
export const SheetChangesPollMs = 30_000;

/*
 * ⛔ The address is a LITERAL template in the call itself: `EndpointCoverageTests` reads the code with a regular
 * expression and does not see addresses assembled by helpers.
 */

/**
 * Change marker of the sheet data for a period. Without `since` — only the current marker (`changed = false`);
 * with it — whether SOMEBODY ELSE changed the visible tables since then (own PATCHes only move the marker).
 */
export function getSheetChanges(
  documentId: number,
  sheetDefId: number,
  periodKey: number,
  since: string | null,
): Promise<SheetChanges> {
  const tail = since === null ? '' : `&since=${encodeURIComponent(since)}`;

  return apiFetch<SheetChanges>(
    `/api/v1/documents/${String(documentId)}/sheets/${String(sheetDefId)}/changes?periodKey=${String(periodKey)}${tail}`,
  );
}

/**
 * `404` (hidden sheet / document not visible) and `422` (period the sheet does not accept): asking again will not
 * help — the poll stops for this sheet and period, without a banner and without a retry storm.
 */
export function isFinalChangesError(error: unknown): boolean {
  return error instanceof EcrApiError && (error.problem.status === 404 || error.problem.status === 422);
}
