/**
 * TC02: key of ONE chat of a document = the table code of the original, `КОД#sheetCopyId` for a copy of the sheet
 * (the same shape the server puts into `TableDiscussionSummaryItemDto.key`, and `sheetStates` of `GET …/tables`).
 *
 * ⛔ A copy and its original share `tableCode`, so every place that finds "the chat of a table" (badge counters, the
 * explicit table choice, query keys) matches by THIS key, never by `tableCode` alone.
 *
 * ⚠ Tiny and import-free: the badge (grid chunk) imports it.
 */
export function chatKey(tableCode: string, sheetCopyId: number | null | undefined): string {
  return sheetCopyId === null || sheetCopyId === undefined ? tableCode : `${tableCode}#${String(sheetCopyId)}`;
}

/** `КОД#id` → the pair; a plain code (or `#instanceId`, the other `?table=` form) → `copyId: null`. */
export function parseChatKey(key: string): { readonly tableCode: string; readonly sheetCopyId: number | null } {
  const match = /^(.+)#(\d+)$/.exec(key);

  return match === null ? { tableCode: key, sheetCopyId: null } : { tableCode: match[1] ?? key, sheetCopyId: Number(match[2]) };
}

/** `sheetCopyId=7` for a copy; empty for the original (the server reads "absent" as the original). Goes AFTER a literal `?`/`&` in the address. */
export function copyParam(sheetCopyId: number | null | undefined): string {
  return sheetCopyId === null || sheetCopyId === undefined ? '' : `sheetCopyId=${String(sheetCopyId)}`;
}
