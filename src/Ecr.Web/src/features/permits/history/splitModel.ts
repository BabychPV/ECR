import { EcrApiError } from '@/api/client';
import type { components } from '@/api/schema';
import type { DocumentPermitHistory, RowPermitHistory } from './api';

/**
 * Feature (j) P3, human 08.11 / mockup approved 09.11: pure helpers of the row case — which cells of a table are a
 * month shared by two permits, the history line of the permit cell, the parts' total, the refusals j3/j4.
 *
 * ⛔ No pro-rata anywhere: ECR never divides a value by days; the user types every part, the total is A + B.
 */
export type SplitMonth = components['schemas']['SplitMonthDto'];
export type SplitCellValue = components['schemas']['CellSplitValueDto'];
export type PermitHistoryItem = components['schemas']['PermitHistoryItemDto'];

/** One month cell of a row that the month splits between permits. */
export interface SplitCellMark {
  readonly rowKey: string;
  readonly columnCode: string;
  readonly month: SplitMonth;
  /** The stored value and the parts; `null` — the server listed the cell only in `columnCodes`. */
  readonly cell: SplitCellValue | null;
  /** Still needs a split: entered data and not every part filled. */
  readonly needsSplit: boolean;
  readonly isSplit: boolean;
}

/** What the grid of one table instance needs: split cells by `rowKey:columnCode`, rows with history by `rowKey`. */
export interface PermitGridMarks {
  readonly cells: ReadonlyMap<string, SplitCellMark>;
  readonly rows: ReadonlyMap<string, RowPermitHistory>;
}

export const NoPermitMarks: PermitGridMarks = { cells: new Map(), rows: new Map() };

export function cellKey(rowKey: string, columnCode: string): string {
  return `${rowKey}:${columnCode}`;
}

/** Rows of ONE table instance (table + sheet copy) with permit history. */
export function rowsOfTable(
  history: DocumentPermitHistory | undefined,
  tableDefId: number,
  sheetCopyId: number | null,
): readonly RowPermitHistory[] {
  return (history?.rows ?? []).filter((row) => row.tableDefId === tableDefId && (row.sheetCopyId ?? null) === sheetCopyId);
}

/** Split cells and history rows of one table instance (`NoPermitMarks` when there is nothing). */
export function permitGridMarksOf(
  history: DocumentPermitHistory | undefined,
  tableDefId: number,
  sheetCopyId: number | null,
): PermitGridMarks {
  const rows = rowsOfTable(history, tableDefId, sheetCopyId);
  if (rows.length === 0) return NoPermitMarks;

  const cells = new Map<string, SplitCellMark>();
  for (const row of rows) {
    for (const month of row.splitMonths) {
      const codes = new Set([...month.cells.map((cell) => cell.columnCode), ...month.columnCodes]);
      for (const columnCode of codes) {
        const cell = month.cells.find((candidate) => candidate.columnCode === columnCode) ?? null;
        const isSplit = cell?.isSplit === true;
        const needsSplit = !isSplit && (month.columnCodes.includes(columnCode) || (cell !== null && cell.total !== null));
        cells.set(cellKey(row.rowKey, columnCode), { rowKey: row.rowKey, columnCode, month, cell, needsSplit, isSplit });
      }
    }
  }

  return { cells, rows: new Map(rows.map((row) => [row.rowKey, row])) };
}

/** `yyyy-MM-dd` → `dd.MM`; anything else as is. */
export function dayMonth(date: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(date);

  return match === null ? date : `${match[3] ?? ''}.${match[2] ?? ''}`;
}

/** `yyyy-MM-dd` → `dd.MM.yyyy`; anything else as is. */
export function dottedDate(date: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(date);

  return match === null ? date : `${match[3] ?? ''}.${match[2] ?? ''}.${match[1] ?? ''}`;
}

/** `dd.MM.yyyy` (server params) → `yyyy-MM-dd`; `null` — not that format. */
export function isoOfDotted(text: unknown): string | null {
  if (typeof text !== 'string') return null;
  const match = /^(\d{2})\.(\d{2})\.(\d{4})$/.exec(text.trim());

  return match === null ? null : `${match[3] ?? ''}-${match[2] ?? ''}-${match[1] ?? ''}`;
}

/** Days in a part, both ends included. */
export function dayCount(from: string, to: string): number {
  const start = Date.parse(`${from.slice(0, 10)}T00:00:00Z`);
  const end = Date.parse(`${to.slice(0, 10)}T00:00:00Z`);
  if (Number.isNaN(start) || Number.isNaN(end) || end < start) return 0;

  return Math.round((end - start) / 86_400_000) + 1;
}

/** `{ year, month }` of a `yyyy-MM-01` month. */
export function monthOf(month: string): { year: number; month: number } {
  return { year: Number(month.slice(0, 4)), month: Number(month.slice(5, 7)) };
}

/** One line of the permit cell's history: the permit and the dates it holds for this row. */
export interface HistoryLine {
  readonly permit: string;
  readonly from: string | null;
  readonly until: string | null;
}

/** The history lines in date order (base permit first), as the server gives them. */
export function historyLinesOf(row: RowPermitHistory): readonly HistoryLine[] {
  return row.permits.map((item) => ({ permit: item.permit, from: item.from, until: item.until }));
}

/**
 * A decimal from the wire (string or number) as canonical text; `null` — empty or not a number.
 *
 * ⚠ Decimals travel as strings (`e470777a`); `Number` is never applied to them here.
 */
export function wireDecimal(value: unknown): string | null {
  const text = typeof value === 'number' && Number.isFinite(value) ? String(value) : typeof value === 'string' ? value.trim() : '';

  return /^-?\d+(\.\d+)?$/.test(text) ? text : null;
}

function scaled(text: string, scale: number): bigint {
  const negative = text.startsWith('-');
  const [whole = '0', fraction = ''] = (negative ? text.slice(1) : text).split('.');
  const digits = BigInt(whole + fraction.padEnd(scale, '0'));

  return negative ? -digits : digits;
}

/** Exact sum of canonical decimals (no float): the month total = A + B. */
export function addDecimals(values: readonly string[]): string {
  const scale = Math.max(0, ...values.map((value) => value.split('.')[1]?.length ?? 0));
  const sum = values.reduce((acc, value) => acc + scaled(value, scale), 0n);
  const negative = sum < 0n;
  const digits = (negative ? -sum : sum).toString().padStart(scale + 1, '0');
  const whole = digits.slice(0, digits.length - scale);
  const fraction = scale === 0 ? '' : digits.slice(digits.length - scale).replace(/0+$/, '');

  return `${negative ? '-' : ''}${whole}${fraction.length > 0 ? `.${fraction}` : ''}`;
}

/** Total of the parts; `null` while a part is empty or not a number. */
export function totalOfParts(parts: readonly (string | null)[]): string | null {
  if (parts.length === 0 || parts.some((part) => part === null)) return null;

  return addDecimals(parts as string[]);
}

/**
 * Initial values of the split dialog: the entered parts; for a cell not split yet, part A gets the old single value
 * (default 3 of the mockup, «was X») and the other parts stay empty — the user moves part of it to B.
 */
export function initialParts(mark: SplitCellMark): readonly (string | null)[] {
  const count = mark.month.parts.length;
  const entered = (mark.cell?.parts ?? []).map(wireDecimal);
  if (entered.some((part) => part !== null)) {
    return Array.from({ length: count }, (_, index) => entered[index] ?? null);
  }

  const single = wireDecimal(mark.cell?.total);

  return Array.from({ length: count }, (_, index) => (index === 0 ? single : null));
}

/** j3: `422 ECR-CELL-4225 splitRequired` — the named row and month cells to split. */
export interface SplitRefusal {
  readonly rowKey: string;
  readonly month: string;
  readonly cells: readonly { rowKey: string; columnCode: string }[];
}

export function splitRefusalOf(error: unknown): SplitRefusal | null {
  if (!(error instanceof EcrApiError) || error.problem.errorCode !== 'ECR-CELL-4225') return null;

  const ext = error.problem.extensions2 ?? {};
  const cells = (Array.isArray(ext['cells']) ? (ext['cells'] as unknown[]) : [])
    .map((raw) => (typeof raw === 'object' && raw !== null ? (raw as Record<string, unknown>) : {}))
    .filter((raw) => typeof raw['rowKey'] === 'string' && typeof raw['columnCode'] === 'string')
    .map((raw) => ({ rowKey: raw['rowKey'] as string, columnCode: raw['columnCode'] as string }));
  const rowKey = typeof ext['rowKey'] === 'string' ? ext['rowKey'] : (cells[0]?.rowKey ?? '');
  if (cells.length === 0 && rowKey === '') return null;

  return { rowKey, month: typeof ext['month'] === 'string' ? ext['month'] : '', cells };
}

/** j4: `422 ECR-CELL-4223 permitGap` — days with no permit, and the start date that closes the gap. */
export interface PermitGapRefusal {
  readonly next: string;
  readonly days: string;
  /** `yyyy-MM-dd`; `null` — the server named none. */
  readonly suggestedFrom: string | null;
}

export function permitGapOf(error: unknown): PermitGapRefusal | null {
  if (!(error instanceof EcrApiError)) return null;
  const ext = error.problem.extensions2 ?? {};
  if (ext['messageKey'] !== 'err.ECR-CELL-4223.permitGap') return null;

  return {
    next: typeof ext['next'] === 'string' ? ext['next'] : '',
    days: typeof ext['days'] === 'string' ? ext['days'] : '',
    suggestedFrom: isoOfDotted(ext['suggestedFrom']),
  };
}
