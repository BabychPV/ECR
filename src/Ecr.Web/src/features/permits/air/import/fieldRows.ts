import type { AirAnalysis, ImportIssue, ImportSpec } from '../airTypes';
import { specFields, withField } from './importModel';

/*
 * The «Mapping» step as the user sees it (UX 20.11): one row per field of the import, with the file column the server matched
 * and a plain state — found / not found (a required field: the user must choose its column) / not in this file (an optional
 * field: nothing to do). Pure, no React: the step and its tests share it.
 */

type Header = NonNullable<AirAnalysis['analysis']['header']>;
export type MatchedColumn = Header['mapped'][number];
export type FileColumn = Header['unmapped'][number] | MatchedColumn;

export type FieldState = 'missing' | 'found' | 'notInFile';

export interface FieldRow {
  /** Field name as the specification (or, without one, the analysis) has it. */
  readonly name: string;
  /** Index in the specification's fields; `null` — the analysis reports a field the specification in hand does not list. */
  readonly index: number | null;
  readonly required: boolean;
  /** The file column the field was read from; `null` — not found. */
  readonly column: MatchedColumn | null;
  readonly state: FieldState;
  /** Why it was not found (the first finding about the field that is not about one row), for the row's hint. */
  readonly issue: ImportIssue | null;
  /** The field has header texts: without an explicit column it is matched by them («automatic»). */
  readonly matchesByHeader: boolean;
}

const StateOrder: Readonly<Record<FieldState, number>> = { missing: 0, found: 1, notInFile: 2 };

function fieldIssue(issues: readonly ImportIssue[], key: string): ImportIssue | null {
  return issues.find((issue) => (issue.row ?? null) === null && (issue.field ?? '').trim().toLowerCase() === key) ?? null;
}

/**
 * Rows of the step: the fields of the specification (names compared without case, as the server does) joined with the columns
 * the analysis matched. Order: what needs a decision first (required, not found), then the found ones from left to right as in
 * the file, then the optional ones the file does not have.
 */
export function fieldRows(spec: ImportSpec | null, mapped: readonly MatchedColumn[], issues: readonly ImportIssue[]): FieldRow[] {
  const byName = new Map<string, MatchedColumn>();
  for (const column of mapped) {
    const key = column.field.trim().toLowerCase();
    if (!byName.has(key)) byName.set(key, column);
  }
  const rows: FieldRow[] = [];
  const seen = new Set<string>();

  specFields(spec ?? {}).forEach((field, index) => {
    const name = (field.name ?? '').trim();
    const key = name.toLowerCase();
    if (name === '' || seen.has(key)) return;
    seen.add(key);
    const column = byName.get(key) ?? null;
    const required = field.required === true;
    rows.push({
      name,
      index,
      required,
      column,
      state: column !== null ? 'found' : required ? 'missing' : 'notInFile',
      issue: column === null ? fieldIssue(issues, key) : null,
      matchesByHeader: (field.patterns ?? []).length > 0,
    });
  });
  for (const column of mapped) {
    const key = column.field.trim().toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    rows.push({ name: column.field, index: null, required: false, column, state: 'found', issue: null, matchesByHeader: false });
  }

  return rows
    .map((row, order) => ({ row, order }))
    .sort(
      (a, b) =>
        StateOrder[a.row.state] - StateOrder[b.row.state] ||
        (a.row.column?.column ?? 0) - (b.row.column?.column ?? 0) ||
        a.order - b.order,
    )
    .map(({ row }) => row);
}

export interface FieldCounts {
  readonly found: number;
  readonly missing: number;
  readonly notInFile: number;
}

export function fieldCounts(rows: readonly FieldRow[]): FieldCounts {
  return {
    found: rows.filter((row) => row.state === 'found').length,
    missing: rows.filter((row) => row.state === 'missing').length,
    notInFile: rows.filter((row) => row.state === 'notInFile').length,
  };
}

/**
 * What the step says at the top: `missing` — required fields without a column (the user chooses them below); `blocked` — the
 * file cannot go on for another reason (the findings say which); `ready` — check and continue.
 */
export type MappingVerdict = 'missing' | 'blocked' | 'ready';

export function mappingVerdict(counts: FieldCounts, canContinue: boolean): MappingVerdict {
  if (counts.missing > 0) return 'missing';

  return canContinue ? 'ready' : 'blocked';
}

/**
 * The field at `index` reads the file column `letter` (an explicit column bypasses the header texts); `null` — back to matching
 * by the header texts. Another field that names the same column explicitly lets it go: one column is read by one field (the
 * server refuses a column claimed twice).
 */
export function withPickedColumn(spec: ImportSpec, index: number, letter: string | null): ImportSpec {
  const value = letter === null ? null : letter.trim().toUpperCase();
  const freed = specFields(spec).reduce<ImportSpec>(
    (current, field, at) =>
      at !== index && value !== null && (field.column ?? '').trim().toUpperCase() === value ? withField(current, at, { column: null }) : current,
    spec,
  );

  return withField(freed, index, { column: value });
}

/** File column letter → the label of the field that reads it now (for «used for …» in the column picker). */
export function columnOwners(rows: readonly FieldRow[], label: (name: string) => string): ReadonlyMap<string, string> {
  const out = new Map<string, string>();
  for (const row of rows) if (row.column !== null) out.set(row.column.columnLetter, label(row.name));

  return out;
}
