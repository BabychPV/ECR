import { useSyncExternalStore } from 'react';

/**
 * Feature (j) P3: «open the split dialog for this cell» — one request at a time for the app.
 *
 * ⛔ A module store, not a prop (the same trick as `inspectedCell.ts`): the grid (lazy `SheetTables` chunk), the save
 * banner (j3), the Issues list and the Inspector all open the SAME dialog, and the dialog host lives next to the
 * Inspector — there is no direct link between those trees.
 */
export interface SplitTarget {
  readonly documentId: number;
  readonly periodKey: number;
  readonly tableInstanceId: number;
  readonly tableDefId: number;
  readonly sheetCopyId: number | null;
  readonly rowKey: string;
  readonly columnCode: string;
}

let current: SplitTarget | null = null;
const listeners = new Set<() => void>();

function publish(next: SplitTarget | null): void {
  current = next;
  for (const listener of listeners) listener();
}

/** Opens the split dialog for the cell. */
export function requestCellSplit(target: SplitTarget): void {
  publish(target);
}

/** Closes it (Cancel, Save, Esc). */
export function closeCellSplit(): void {
  if (current !== null) publish(null);
}

/** The requested cell; `null` — the dialog is closed. */
export function cellSplitRequest(): SplitTarget | null {
  return current;
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);

  return () => {
    listeners.delete(listener);
  };
}

export function useCellSplitRequest(): SplitTarget | null {
  return useSyncExternalStore(subscribe, cellSplitRequest, cellSplitRequest);
}
