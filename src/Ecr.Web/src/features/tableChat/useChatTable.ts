import type { DocumentTableDto } from '@/api/types';
import { useInspectedCell } from '@/features/documents/inspector/inspectedCell';
import { resolveTable } from '@/features/grid/tableTreeModel';
import { useUrlState } from '@/shared/ui/useUrlState';
import { useChatTableOverride } from './chatStore';

/**
 * The table the chat is bound to: an explicit choice (badge click) → the table of the last focused cell → `?table=`
 * → the first table of the active sheet. Only tables the user SEES are candidates (`GET …/tables` already filters
 * hidden ones), so a hidden table never gets a chat.
 */
export function useChatTable(
  tables: readonly DocumentTableDto[],
  activeTables: readonly DocumentTableDto[],
  periodKey: number,
): DocumentTableDto | undefined {
  const override = useChatTableOverride();
  const cell = useInspectedCell();
  const [tableKey] = useUrlState('table');

  const explicit = override === null ? undefined : tables.find((table) => table.tableCode === override);
  if (explicit !== undefined) return explicit;

  const focused =
    cell === null || cell.periodKey !== periodKey
      ? undefined
      : tables.find((table) => table.tableInstanceId === cell.tableInstanceId);
  if (focused !== undefined) return focused;

  // ⚠ `resolveTable` falls back to the first table of the list it gets, so the key is matched here: a key that
  // names no table must lead to the ACTIVE sheet's first table, not to the first table of the whole document.
  const keyed =
    tableKey === null || tableKey.length === 0
      ? undefined
      : tables.find((table) =>
          tableKey.startsWith('#') ? String(table.tableInstanceId) === tableKey.slice(1) : table.tableCode === tableKey,
        );

  return keyed ?? resolveTable(null, activeTables) ?? tables[0];
}
