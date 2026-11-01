import type { QueryClient } from '@tanstack/react-query';
import { cancelAutosave, scheduleAutosave } from '../autosave';
import { whenInFlightSettles } from '../inFlightEdits';
import { hasSendablePending } from '../pendingStore';
import { refreshSlices } from '../sliceCache';

/**
 * LIVE-REFRESH B3: re-read tables of the sheet from the server WITHOUT touching the user's own edits.
 *
 * Order (each step is load-bearing):
 *  1. one frame — an open cell editor that lost focus to the button commits its value into the pending store;
 *  2. wait for own PATCHes in flight — a refetch started before one of them commits could read the OLD row and
 *     land after the PATCH answer, which already took the edit out of the pending store: the saved value would
 *     vanish from the screen;
 *  3. hold the autosave for the duration of the refetch (same race, other direction) and plan it again after;
 *  4. refetch the named slices (`refreshSlices`: the grid's own cache keys, no page reload).
 *
 * ⚠ Unsaved edits are never sent, dropped or overwritten here: they live in `pendingStore`, not in the slice, and
 * the grid paints them over whatever the server returns. A cell somebody else changed under an unsaved edit goes
 * the existing conflict way (`knownVersions.ts`, AN-39/L8-20: the row keeps its old version → `409` → dialog).
 */
export async function refreshSheetTables(
  client: QueryClient,
  periodKey: number,
  tableInstanceIds: Iterable<number>,
): Promise<void> {
  await nextFrame();
  await whenInFlightSettles();

  cancelAutosave();
  try {
    await refreshSlices(client, periodKey, tableInstanceIds);
  } finally {
    if (hasSendablePending()) scheduleAutosave();
  }
}

/** A frame, with a fallback: `requestAnimationFrame` does not fire in a background tab. */
function nextFrame(): Promise<void> {
  return new Promise((resolve) => {
    const fallback = setTimeout(resolve, 100);
    requestAnimationFrame(() => {
      clearTimeout(fallback);
      setTimeout(resolve, 0);
    });
  });
}
