import { useSyncExternalStore } from 'react';
import { chatKey } from './chatKey';
import type { DiscussionSummaryItem } from './types';

/**
 * Two tiny module stores of the table chat (the technique of `inspector/inspectedCell.ts`).
 *
 * ⛔ Module stores, not props or a query in every consumer: the badge lives in the grid chunk (`SheetTables`), the
 * button in the inspector chunk, the panel in its own lazy chunk, and there is no React path between them. The
 * module is tiny and imports nothing heavy, so it does not touch the route budget (`D-132`). It also keeps the grid
 * free of a `QueryClient` dependency: the badges only READ what the button published.
 */

/** `?panel=` value that opens the chat panel (the inspector tabs use `issues|history|info` in the same parameter). */
export const DiscussionPanelValue = 'discussion';

const listeners = new Set<() => void>();

function emit(): void {
  for (const listener of listeners) listener();
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);

  return () => {
    listeners.delete(listener);
  };
}

// --- the table the user chose explicitly (a click on a badge) ------------------------------------------------

/**
 * ⚠ Without an explicit choice the panel follows the table of the last focused cell, then `?table=`, then the
 * first table of the sheet (`useChatTable`).
 */
let override: string | null = null;

/** `chatKey` of the chosen table: `КОД` for the original, `КОД#sheetCopyId` for a copy (TC02). */
export function setChatTable(key: string | null): void {
  if (override === key) return;
  override = key;
  emit();
}

export function chatTableOverride(): string | null {
  return override;
}

export function useChatTableOverride(): string | null {
  return useSyncExternalStore(subscribe, chatTableOverride, chatTableOverride);
}

// --- "a human opened it": the panel takes the focus; opened from the address on load, it does not ----------------

let focusRequested = false;

/** The button/badge calls it right before opening: the panel that mounts next takes the focus. */
export function requestChatFocus(): void {
  focusRequested = true;
}

/** Read once by the panel host at mount. */
export function consumeChatFocus(): boolean {
  const requested = focusRequested;
  focusRequested = false;

  return requested;
}

// --- "open the panel": the badge (grid chunk, no router in its tests) asks, the host (inside the router) acts ------

let openRequests = 0;

/**
 * A badge asks to open the panel. ⚠ The address (`?panel=discussion`) is written by the host, not here: the grid
 * must not depend on the router, and the host already owns the `panel` parameter.
 */
export function requestChatOpen(): void {
  openRequests += 1;
  emit();
}

function openRequestCount(): number {
  return openRequests;
}

export function useChatOpenRequests(): number {
  return useSyncExternalStore(subscribe, openRequestCount, openRequestCount);
}

// --- summary of the document published by the header button ----------------------------------------------------

let summaryDocumentId: number | null = null;
let summaryItems: ReadonlyMap<string, DiscussionSummaryItem> = new Map();

/** `null` clears (the button unmounts or the chat is unavailable). */
export function publishSummary(documentId: number, items: readonly DiscussionSummaryItem[] | null): void {
  summaryDocumentId = items === null ? null : documentId;
  // TC02: by the chat KEY, never by `tableCode` - a copy of a sheet and its original share the code.
  summaryItems = new Map((items ?? []).map((item) => [item.key, item]));
  emit();
}

/** Summary line of one table (of one copy of it); `undefined` — no messages there, or the chat is unavailable. */
export function useTableSummaryItem(
  documentId: number,
  tableCode: string,
  sheetCopyId: number | null = null,
): DiscussionSummaryItem | undefined {
  const key = chatKey(tableCode, sheetCopyId);

  return useSyncExternalStore(
    subscribe,
    () => (summaryDocumentId === documentId ? summaryItems.get(key) : undefined),
    () => undefined,
  );
}
