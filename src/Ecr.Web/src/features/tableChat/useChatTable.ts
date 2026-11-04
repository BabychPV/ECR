import type { DocumentTableDto } from '@/api/types';
import { useInspectedCell } from '@/features/documents/inspector/inspectedCell';
import { resolveTable } from '@/features/grid/tableTreeModel';
import { useUrlState } from '@/shared/ui/useUrlState';
import { chatKey, parseChatKey } from './chatKey';
import { useChatTableOverride } from './chatStore';

/** The chat key of a table: its code, plus the copy of the sheet it belongs to (TC02). */
export function chatKeyOf(table: Pick<DocumentTableDto, 'tableCode' | 'sheetCopyId'>): string {
  return chatKey(table.tableCode, table.sheetCopyId ?? null);
}

/**
 * The table the chat is bound to: an explicit choice (badge click) → the table of the last focused cell → `?table=`
 * → the first table of the active sheet. Only tables the user SEES are candidates (`GET …/tables` already filters
 * hidden ones), so a hidden table never gets a chat.
 *
 * ⛔ TC02: a copy of a sheet has the same `tableCode` as its original, so the explicit choice and `?table=` are
 * matched by the chat key (`КОД` / `КОД#sheetCopyId`), and a bare code prefers the ACTIVE sheet's table.
 */
export function useChatTable(
  tables: readonly DocumentTableDto[],
  activeTables: readonly DocumentTableDto[],
  periodKey: number,
): DocumentTableDto | undefined {
  const override = useChatTableOverride();
  const cell = useInspectedCell();
  const [tableKey] = useUrlState('table');

  const explicit = override === null ? undefined : tables.find((table) => chatKeyOf(table) === override);
  if (explicit !== undefined) return explicit;

  const focused =
    cell === null || cell.periodKey !== periodKey
      ? undefined
      : tables.find((table) => table.tableInstanceId === cell.tableInstanceId);
  if (focused !== undefined) return focused;

  // ⚠ `resolveTable` falls back to the first table of the list it gets, so the key is matched here: a key that
  // names no table must lead to the ACTIVE sheet's first table, not to the first table of the whole document.
  const matches = (table: DocumentTableDto): boolean => {
    if (tableKey === null || tableKey.length === 0) return false;
    if (tableKey.startsWith('#')) return String(table.tableInstanceId) === tableKey.slice(1);

    const parsed = parseChatKey(tableKey);

    // A bare code (what the grid writes for the selected table) names the table of whichever sheet is active.
    return table.tableCode === parsed.tableCode && (parsed.sheetCopyId === null || (table.sheetCopyId ?? null) === parsed.sheetCopyId);
  };
  const keyed = activeTables.find(matches) ?? tables.find(matches);

  return keyed ?? resolveTable(null, activeTables) ?? tables[0];
}
