import type { TemplateStructureDto } from '@/api/types';
import { localized } from '@/shared/i18n/localized';
import type { ScriptCompletionBase } from './ScriptEditor';
import type { ScriptFunctionInfo, ScriptInputTable } from './scriptCompletion';
import type { ScriptOutlineDto } from './types';

/**
 * Збірка контексту підказок редактора з відповідей наявних API (структура
 * версії шаблону, метадані виразів). Чисті функції.
 */

export interface ScriptTemplateSource {
  readonly templateCode: string;
  readonly structure: Pick<TemplateStructureDto, 'sheets'>;
}

interface TemplateRef {
  readonly id: number;
  readonly code: string;
  readonly isArchived: boolean;
}

interface VersionRef {
  readonly id: number;
  readonly status: string;
}

/** Коди шаблонів, названі в скрипті (`Шаблон.Таблиця`). */
export function namedTemplateCodes(outline: ScriptOutlineDto | null): string[] {
  const codes = new Set<string>();
  for (const table of outline?.tables ?? []) {
    if (table.templateCode !== null && table.templateCode !== '') codes.add(table.templateCode);
  }
  return [...codes];
}

/**
 * Які шаблони читати за структурою. Скрипт назвав шаблон — лише він; ні — усі
 * неархівні (API «колонки за кодом таблиці» немає, тож код таблиці шукається
 * по чинних версіях).
 */
export function templatesToRead<T extends TemplateRef>(
  outline: ScriptOutlineDto | null,
  templates: readonly T[],
): T[] {
  if ((outline?.tables.length ?? 0) === 0) return [];

  const live = templates.filter((template) => !template.isArchived);
  const named = namedTemplateCodes(outline);
  if (named.length === 0) return live;

  const picked = live.filter((template) => named.includes(template.code));
  return picked.length === 0 ? live : picked;
}

/** Чинна версія шаблону: опублікована з найбільшим Id; `null` — опублікованої немає. */
export function liveTemplateVersionId(versions: readonly VersionRef[]): number | null {
  let best: number | null = null;
  for (const version of versions) {
    if (version.status !== 'Published') continue;
    if (best === null || version.id > best) best = version.id;
  }
  return best;
}

/** Таблиці з outline → їхні колонки зі структур шаблонів. */
export function inputTablesFor(
  outline: ScriptOutlineDto | null,
  sources: readonly ScriptTemplateSource[],
): ScriptInputTable[] {
  const result: ScriptInputTable[] = [];
  const seen = new Set<string>();

  for (const table of outline?.tables ?? []) {
    if (seen.has(table.code)) continue;

    const candidates =
      table.templateCode === null || table.templateCode === ''
        ? sources
        : sources.filter((source) => source.templateCode === table.templateCode);

    for (const source of candidates) {
      const found = source.structure.sheets
        .flatMap((sheet) => sheet.tables)
        .find((candidate) => candidate.code === table.code);
      if (found === undefined) continue;

      seen.add(table.code);
      result.push({
        tableCode: table.code,
        columns: found.columns.map((column) => ({
          code: column.code,
          header: localized(column.headerL10n),
          dataType: column.dataType,
          isMonthColumn: false,
        })),
      });
      break;
    }
  }

  return result;
}

export interface MetadataFunction {
  readonly name: string;
  readonly resultType: string | null;
  readonly minArgs: number;
  readonly maxArgs: number | null;
}

/** Підпис функції для підказки: `Name(1…) → Number`. */
export function functionInfo(fn: MetadataFunction): ScriptFunctionInfo {
  let arity: string;
  if (fn.maxArgs === null) arity = `${String(fn.minArgs)}…`;
  else if (fn.maxArgs === fn.minArgs) arity = String(fn.minArgs);
  else arity = `${String(fn.minArgs)}…${String(fn.maxArgs)}`;
  const result = fn.resultType === null ? '' : ` → ${fn.resultType}`;

  return { name: fn.name, signature: `${fn.name}(${arity})${result}` };
}

/** Усе, що не залежить від курсора, для провайдера підказок. */
export function assembleCompletionBase(parts: {
  readonly outline: ScriptOutlineDto | null;
  readonly sources: readonly ScriptTemplateSource[];
  readonly functions: readonly MetadataFunction[];
  readonly headerFields: readonly string[];
}): ScriptCompletionBase {
  return {
    outline: parts.outline,
    tables: inputTablesFor(parts.outline, parts.sources),
    functions: parts.functions.map(functionInfo),
    headerFields: parts.headerFields,
  };
}
