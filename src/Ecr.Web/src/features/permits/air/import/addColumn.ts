import { EcrApiError } from '@/api/client';
import type { ImportFieldSpec, ImportSpec } from '../airTypes';
import {
  getPermitType,
  openPermitTypeVersion,
  publishPermitType,
  replacePermitType,
} from '@/features/permits/api/typesApi';
import type { PermitType, PermitTypeColumn } from '@/features/permits/api/types';

/*
 * «Add as new column» (P1 06.11): a file column that no field reads becomes a NEW column of the permit's sources (or
 * substances) table, through the same permit-type API the type editor uses (new version → replace → publish), and a profile
 * field with the column's CODE reads it (the server stores a field whose name equals a column code of the table).
 */

/** AIR type and its two import tables (AirLimitsSchema). */
export const AirType = 'AIR';
export const SourcesTable = 'AIR_SOURCES';
export const SubstancesTable = 'AIR_SOURCE_SUBSTANCES';

export type NewColumnKind = 'text' | 'number' | 'whole' | 'date';
export type NewColumnLevel = 'Parent' | 'Child';

export const NewColumnKinds: readonly NewColumnKind[] = ['text', 'number', 'whole', 'date'];

export interface NewColumnRequest {
  /** File column letter (`B`). */
  readonly letter: string;
  /** Name the user sees in the table. */
  readonly name: string;
  readonly kind: NewColumnKind;
  /** `Parent` — one value per source (sources table); `Child` — per substance row (substances table). */
  readonly level: NewColumnLevel;
}

const DataTypes: Readonly<Record<NewColumnKind, string>> = { text: 'String', number: 'Decimal', whole: 'Int', date: 'Date' };
const Parsers: Readonly<Record<NewColumnKind, NonNullable<ImportFieldSpec['parser']>>> = {
  text: 'Text',
  number: 'Decimal',
  whole: 'Integer',
  date: 'Date',
};

export function tableFor(level: NewColumnLevel): string {
  return level === 'Parent' ? SourcesTable : SubstancesTable;
}

/** Name for the form: the file header without line breaks and extra spaces, at most 100 characters. */
export function suggestedName(headerText: string): string {
  return headerText.replace(/\s+/g, ' ').trim().slice(0, 100);
}

/**
 * Column code: `FILE_<letter>`, or `FILE_<letter>_2`, `_3`, … when that code is taken in the table. A column that already
 * exists with the SAME name is reused (re-import of the same file adds nothing).
 */
export function columnCode(existing: readonly PermitTypeColumn[], letter: string, name: string): { code: string; reuse: boolean } {
  const base = `FILE_${letter.toUpperCase()}`;
  const byCode = new Map(existing.map((column) => [column.code.toUpperCase(), column] as const));

  for (let n = 1; ; n++) {
    const code = n === 1 ? base : `${base}_${String(n)}`;
    const found = byCode.get(code);
    if (found === undefined) return { code, reuse: false };
    if (Object.values(found.nameL10n).some((label) => label.trim() === name.trim())) return { code, reuse: true };
  }
}

/** The new column as the type editor would write it. */
export function newColumn(code: string, request: NewColumnRequest, order: number): PermitTypeColumn {
  const decimal = request.kind === 'number';

  return {
    code,
    // F13-p198 P3-2: all three catalogue languages — the name the user typed is the only one known (kz = en until translated).
    nameL10n: { en: request.name, ru: request.name, kz: request.name },
    dataType: DataTypes[request.kind],
    order,
    required: false,
    key: false,
    choices: null,
    group: null,
    listRegistryCode: null,
    listScope: null,
    precision: decimal ? 28 : null,
    scale: decimal ? 10 : null,
    siblingTableCode: null,
    unitId: null,
  };
}

/** The profile field that reads the file column into the new table column (by explicit letter, not by header text). */
export function fieldFor(code: string, request: NewColumnRequest): ImportFieldSpec {
  return { name: code, level: request.level, column: request.letter.toUpperCase(), patterns: [], parser: Parsers[request.kind] };
}

/** The spec with the new field (a field of the same name is replaced: the same column mapped again). */
export function withColumnField(spec: ImportSpec, field: ImportFieldSpec): ImportSpec {
  const others = (spec.fields ?? []).filter((existing) => existing.name !== field.name);

  return { ...spec, fields: [...others, field] };
}

/** Why the column cannot be added now; `null` — it can. */
export type AddColumnProblem = 'draftOpen' | 'noTable' | null;

export function addColumnProblem(type: PermitType, table: string): AddColumnProblem {
  // Someone else's unpublished changes would be published together with ours: refuse instead.
  if (type.hasUnpublishedChanges) return 'draftOpen';
  if (!type.tables.some((candidate) => candidate.code === table)) return 'noTable';

  return null;
}

export class AddColumnError extends Error {
  constructor(readonly problem: Exclude<AddColumnProblem, null>) {
    super(problem);
    this.name = 'AddColumnError';
  }
}

/**
 * Adds the column to the published AIR type (new version → replace → publish) and returns its code; a column with the same
 * code and name is reused without a new version.
 */
export async function addPermitTableColumn(request: NewColumnRequest, reason: string): Promise<string> {
  const table = tableFor(request.level);
  const current = await getPermitType(AirType);
  const target = current.tables.find((candidate) => candidate.code === table);
  if (target !== undefined) {
    const { code, reuse } = columnCode(target.columns, request.letter, request.name);
    if (reuse) return code;
  }

  const problem = addColumnProblem(current, table);
  if (problem !== null) throw new AddColumnError(problem);

  // F13-p198 P3-1: the server is the guard — a draft opened by someone else after our read makes new-version 409
  // (ECR-PRM-4093), and that is the same «someone else's draft is open» refusal, not a generic error.
  const draft = await openPermitTypeVersion(AirType).catch((error: unknown) => {
    if (error instanceof EcrApiError && error.problem.errorCode === 'ECR-PRM-4093') throw new AddColumnError('draftOpen');
    throw error;
  });
  const draftTable = draft.tables.find((candidate) => candidate.code === table);
  if (draftTable === undefined) throw new AddColumnError('noTable');

  const { code } = columnCode(draftTable.columns, request.letter, request.name);
  const order = Math.max(0, ...draftTable.columns.map((column) => column.order)) + 1;
  const tables = draft.tables.map((candidate) =>
    candidate.code === table ? { ...candidate, columns: [...candidate.columns, newColumn(code, request, order)] } : candidate,
  );

  const replaced = await replacePermitType(AirType, {
    rowVersion: draft.rowVersion,
    nameL10n: draft.nameL10n,
    category: draft.category,
    rules: draft.rules,
    tables,
  });
  await publishPermitType(AirType, { reason, rowVersion: replaced.rowVersion });

  return code;
}
