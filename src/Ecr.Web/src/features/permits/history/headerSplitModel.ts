import type { DocumentTableDto } from '@/api/types';
import type { DocumentPermitHistory, RowPermitHistory } from './api';
import { wireDecimal, type SplitCellValue, type SplitMonth } from './splitModel';

/**
 * Feature (j) Ship 2 (jh2/jh3): pure helpers of the HEADER case — the rows that rely on the document header permit
 * (`permitSource = header`) and the month cells a mid-month header change makes them split.
 */

/** One month cell of a row relying on the header permit (a line of the «Split all…» dialog). */
export interface HeaderSplitLine {
  readonly row: RowPermitHistory;
  /** The table instance of the row in this period; `null` — the page does not list it (no save possible). */
  readonly table: DocumentTableDto | null;
  readonly month: SplitMonth;
  readonly columnCode: string;
  readonly cell: SplitCellValue | null;
  readonly needsSplit: boolean;
  readonly isSplit: boolean;
}

/** Rows of the history that rely on the header permit. */
export function headerRowsOf(history: DocumentPermitHistory | undefined): readonly RowPermitHistory[] {
  return (history?.rows ?? []).filter((row) => row.permitSource === 'header');
}

/** The table instance of a history row among the page's tables (same table and sheet copy). */
export function tableOfRow(tables: readonly DocumentTableDto[] | undefined, row: RowPermitHistory): DocumentTableDto | null {
  return (
    (tables ?? []).find(
      (table) => table.tableDefId === row.tableDefId && (table.sheetCopyId ?? null) === (row.sheetCopyId ?? null),
    ) ?? null
  );
}

/**
 * The lines of the bulk split: every month cell the server lists to split, plus cells already (partly) split — so a
 * partial save can be finished later. A calculated value (listed by neither) is never a line (F13 p220 j7).
 */
export function headerSplitLinesOf(
  history: DocumentPermitHistory | undefined,
  tables: readonly DocumentTableDto[] | undefined,
): readonly HeaderSplitLine[] {
  const lines: HeaderSplitLine[] = [];
  for (const row of headerRowsOf(history)) {
    for (const month of row.splitMonths) {
      const entered = month.cells.filter((cell) => cell.isSplit || cell.parts.some((part) => wireDecimal(part) !== null));
      const codes = [...new Set([...month.columnCodes, ...entered.map((cell) => cell.columnCode)])];
      for (const columnCode of codes) {
        const cell = month.cells.find((candidate) => candidate.columnCode === columnCode) ?? null;
        const isSplit = cell?.isSplit === true;
        lines.push({
          row,
          table: tableOfRow(tables, row),
          month,
          columnCode,
          cell,
          isSplit,
          needsSplit: !isSplit && month.columnCodes.includes(columnCode),
        });
      }
    }
  }

  return lines;
}

/** jh2 banner: rows still to split, by table (in the order the page lists the tables). */
export interface HeaderSplitTableCount {
  readonly tableDefId: number;
  readonly sheetCopyId: number | null;
  readonly table: DocumentTableDto | null;
  readonly rows: number;
}

export function pendingRowsByTable(lines: readonly HeaderSplitLine[]): readonly HeaderSplitTableCount[] {
  const byTable = new Map<string, { count: HeaderSplitTableCount; rows: Set<string> }>();
  for (const line of lines.filter((candidate) => candidate.needsSplit)) {
    const key = `${String(line.row.tableDefId)}#${String(line.row.sheetCopyId ?? '')}`;
    const entry = byTable.get(key) ?? {
      count: { tableDefId: line.row.tableDefId, sheetCopyId: line.row.sheetCopyId ?? null, table: line.table, rows: 0 },
      rows: new Set<string>(),
    };
    entry.rows.add(line.row.rowKey);
    byTable.set(key, { count: { ...entry.count, rows: entry.rows.size }, rows: entry.rows });
  }

  return [...byTable.values()].map((entry) => entry.count);
}
