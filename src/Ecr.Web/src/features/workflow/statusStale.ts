import { useSyncExternalStore } from 'react';

/**
 * ✎ LIVE-STATUS: "the approval state on the screen is known to be old" for a document × period.
 *
 * The live poll (grid chunk) sees that someone else moved the sheet (submit, reject, approve, step, reopen) and
 * re-reads the state; until the new state arrives the action bar must not offer an action against the old one.
 * ⚠ Not a query: no data here, only a flag between two components that do not share props (the poll lives in
 * `SheetWorkspace`, the buttons in `DocumentActionBar`); the data itself stays under its own query keys.
 */
const stale = new Map<string, number>();
const listeners = new Set<() => void>();

function keyOf(documentId: number, periodKey: number): string {
  return `${String(documentId)}:${String(periodKey)}`;
}

function emit(): void {
  for (const listener of listeners) listener();
}

/** Marks the state stale while `work` runs (re-read of the state); nested marks are counted. */
export async function whileStatusStale(documentId: number, periodKey: number, work: () => Promise<void>): Promise<void> {
  const key = keyOf(documentId, periodKey);
  stale.set(key, (stale.get(key) ?? 0) + 1);
  emit();
  try {
    await work();
  } finally {
    const left = (stale.get(key) ?? 1) - 1;
    if (left <= 0) stale.delete(key);
    else stale.set(key, left);
    emit();
  }
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);

  return () => listeners.delete(listener);
}

/** `true` — the state of this document × period is being re-read after someone else changed it. */
export function useStatusStale(documentId: number, periodKey: number): boolean {
  const key = keyOf(documentId, periodKey);

  return useSyncExternalStore(subscribe, () => stale.has(key), () => false);
}
