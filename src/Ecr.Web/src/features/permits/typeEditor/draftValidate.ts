import type { PermitTypeColumn, PermitTypeRule, PermitTypeTable } from '../api/types';
import { ColumnDataTypes, ListScopes, ReservedColumnCode, isValidCode, type TypeDraft } from './draftTypes';

/*
 * Перевірки робочої копії до збереження — дзеркало `PermitTypeDefinitionValidator` сервера (PM-03).
 * Ключі `messageKey` ТІ САМІ (`err.ECR-PRM-0422.*`), тож текст береться з того самого каталогу, що й
 * відмова сервера: людина читає одне речення і до `PUT`, і після нього.
 *
 * Сервер зупиняється на першій відмові, клієнт збирає ВСІ — по одній на стовпець, щоб не заливати
 * екран. Перевірки, яким потрібна база (існування спільного довідника, одиниця), тут не живуть.
 */

/** Одна відмова: ключ каталогу, підстановки й адреса в моделі (щоб підсвітити рядок). */
export interface DraftProblem {
  readonly messageKey: string;
  readonly params: Readonly<Record<string, string | number>>;
  readonly tableCode?: string;
  readonly columnCode?: string;
}

export interface ValidateOptions {
  /** Код типу: з нього складаються коди реєстрів (`PRM_<тип>_<таблиця>`), обмежені 64 символами. */
  readonly typeCode: string;
  /** Опублікований склад, якщо відомий: опублікованого не можна зламати. */
  readonly published?: TypeDraft | null | undefined;
}

const Categories = ['Air', 'Water', 'Waste', 'Other'];
const Severities = ['Warn', 'Block'];

const key = (name: string): string => `err.ECR-PRM-0422.${name}`;
const hasText = (text: Readonly<Record<string, string>> | null | undefined): boolean =>
  text !== null && text !== undefined && Object.values(text).some((value) => value.trim() !== '');

function problem(
  name: string,
  params: Record<string, string | number> = {},
  where: { tableCode?: string; columnCode?: string } = {},
): DraftProblem {
  return { messageKey: key(name), params, ...where };
}

/** Код реєстру вміщується в довжину коду (64). */
const registryCodeOk = (code: string): boolean => isValidCode(code);

function listProblem(typeCode: string, table: PermitTypeTable, column: PermitTypeColumn, tableCodes: Set<string>): DraftProblem | null {
  const where = { tableCode: table.code, columnCode: column.code };
  const lookup = column.dataType === 'Lookup';
  const hasListData =
    column.listScope !== null || column.listRegistryCode !== null || column.siblingTableCode !== null || column.choices !== null;
  if (!lookup) return hasListData ? problem('listForNonLookup', { columnCode: column.code }, where) : null;

  const inconsistent = problem('listInconsistent', { columnCode: column.code }, where);
  switch (column.listScope) {
    case ListScopes.shared:
      return (column.listRegistryCode ?? '').trim() === '' || column.siblingTableCode !== null || column.choices !== null
        ? inconsistent
        : null;
    case ListScopes.type: {
      if (column.siblingTableCode !== null) return inconsistent;
      const choices = column.choices ?? [];
      if (choices.length === 0) return problem('typeOnlyNoChoices', { columnCode: column.code }, where);
      const lower = choices.map((choice) => choice.trim().toLowerCase());
      if (lower.some((choice) => choice === '') || new Set(lower).size !== lower.length) {
        return problem('choicesInvalid', { columnCode: column.code }, where);
      }
      const listCode = `PRM_${typeCode}_${table.code}_${column.code}`;
      return registryCodeOk(listCode) ? null : problem('registryCodeInvalid', { registryCode: listCode }, where);
    }
    case ListScopes.permit: {
      const sibling = column.siblingTableCode ?? '';
      if (sibling.trim() === '' || column.listRegistryCode !== null || column.choices !== null) return inconsistent;
      if (sibling.toLowerCase() === table.code.toLowerCase() || !tableCodes.has(sibling.toLowerCase())) {
        return problem('siblingUnknown', { columnCode: column.code, siblingTableCode: sibling }, where);
      }
      return null;
    }
    default:
      return inconsistent;
  }
}

function numericProblem(column: PermitTypeColumn, tableCode: string): DraftProblem | null {
  if (column.precision === null && column.scale === null) return null;
  const ok =
    column.dataType === 'Decimal' &&
    column.precision !== null &&
    column.precision >= 1 &&
    column.precision <= 38 &&
    (column.scale === null || column.scale <= column.precision);
  return ok ? null : problem('numericFormat', { columnCode: column.code }, { tableCode, columnCode: column.code });
}

/** Перша відмова стовпця за порядком сервера. */
function columnProblem(
  typeCode: string,
  table: PermitTypeTable,
  column: PermitTypeColumn,
  seen: Set<string>,
  tableCodes: Set<string>,
): DraftProblem | null {
  const where = { tableCode: table.code, columnCode: column.code };
  if (!isValidCode(column.code)) return problem('columnCodeInvalid', { tableCode: table.code, columnCode: column.code }, where);
  if (column.code.toLowerCase() === ReservedColumnCode.toLowerCase()) {
    return problem('columnCodeReserved', { tableCode: table.code, columnCode: column.code }, where);
  }
  if (seen.has(column.code.toLowerCase())) {
    return problem('columnDuplicate', { tableCode: table.code, columnCode: column.code }, where);
  }
  seen.add(column.code.toLowerCase());
  if (!hasText(column.nameL10n)) return problem('nameRequired', { item: column.code }, where);
  if (!(ColumnDataTypes as readonly string[]).includes(column.dataType)) {
    return problem('columnTypeUnknown', { columnCode: column.code, dataType: column.dataType }, where);
  }
  if (column.group !== null && !isValidCode(column.group)) {
    return problem('groupCodeInvalid', { columnCode: column.code, group: column.group }, where);
  }
  return numericProblem(column, table.code) ?? listProblem(typeCode, table, column, tableCodes);
}

function ruleProblems(rules: readonly PermitTypeRule[]): DraftProblem[] {
  const out: DraftProblem[] = [];
  const seen = new Set<string>();
  for (const rule of rules) {
    if (!isValidCode(rule.code)) out.push(problem('ruleCodeInvalid', { ruleCode: rule.code }));
    else if (seen.has(rule.code.toLowerCase())) out.push(problem('ruleDuplicate', { ruleCode: rule.code }));
    else if (rule.expression.trim() === '') out.push(problem('ruleExpressionRequired', { ruleCode: rule.code }));
    else if (!Severities.includes(rule.severity)) {
      out.push(problem('ruleSeverityUnknown', { ruleCode: rule.code, severity: rule.severity }));
    } else if (!hasText(rule.nameL10n)) out.push(problem('nameRequired', { item: rule.code }));
    seen.add(rule.code.toLowerCase());
  }
  return out;
}

/** Таблиця, що через інші посилається на себе, не публікується (`siblingCycle`). */
function cycleProblem(tables: readonly PermitTypeTable[]): DraftProblem | null {
  const known = new Set(tables.map((table) => table.code.toLowerCase()));
  // Самопосилання й невідомі таблиці вже названі siblingUnknown — у коло їх не беремо (як сервер).
  const edges = new Map(
    tables.map((table) => [
      table.code.toLowerCase(),
      table.columns.flatMap((column) => {
        const target = column.siblingTableCode?.toLowerCase();
        return target === undefined || target === table.code.toLowerCase() || !known.has(target) ? [] : [target];
      }),
    ]),
  );
  const state = new Map<string, 1 | 2>();
  const path: string[] = [];
  let chain: string | null = null;

  const visit = (code: string): void => {
    if (chain !== null) return;
    const seen = state.get(code);
    if (seen === 2) return;
    if (seen === 1) {
      chain = [...path.slice(path.indexOf(code)), code].join(' → ');
      return;
    }
    state.set(code, 1);
    path.push(code);
    for (const next of edges.get(code) ?? []) visit(next);
    path.pop();
    state.set(code, 2);
  };
  for (const table of tables) visit(table.code.toLowerCase());
  return chain === null ? null : problem('siblingCycle', { chain });
}

function publishedProblems(draft: TypeDraft, published: TypeDraft): DraftProblem[] {
  const out: DraftProblem[] = [];
  for (const before of published.tables) {
    const after = draft.tables.find((table) => table.code.toLowerCase() === before.code.toLowerCase());
    if (after === undefined) {
      out.push(problem('tableRemoved', { tableCode: before.code }, { tableCode: before.code }));
      continue;
    }
    for (const column of before.columns) {
      const next = after.columns.find((c) => c.code.toLowerCase() === column.code.toLowerCase());
      const where = { tableCode: before.code, columnCode: column.code };
      if (next === undefined) out.push(problem('columnRemoved', { tableCode: before.code, columnCode: column.code }, where));
      else if (next.dataType !== column.dataType) {
        out.push(problem('columnRetyped', { tableCode: before.code, columnCode: column.code }, where));
      }
    }
    const keys = (table: PermitTypeTable): string =>
      table.columns
        .filter((c) => c.key)
        .map((c) => c.code.toLowerCase())
        .sort()
        .join('|');
    if (keys(before) !== keys(after)) out.push(problem('keyChanged', { tableCode: before.code }, { tableCode: before.code }));
  }
  return out;
}

/** Усі відмови складу типу. Порожній масив — сервер прийме `PUT`. */
export function validateDraft(draft: TypeDraft, options: ValidateOptions): DraftProblem[] {
  const out: DraftProblem[] = [];
  if (!hasText(draft.nameL10n)) out.push(problem('nameRequired', { item: 'type' }));
  if (!Categories.includes(draft.category)) out.push(problem('categoryUnknown', { category: draft.category }));
  if (!registryCodeOk(`PRM_${options.typeCode}`)) {
    out.push(problem('registryCodeInvalid', { registryCode: `PRM_${options.typeCode}` }));
  }

  const tableCodes = new Set<string>();
  for (const table of draft.tables) {
    const lower = table.code.toLowerCase();
    const registryCode = `PRM_${options.typeCode}_${table.code}`;
    if (!isValidCode(table.code)) out.push(problem('tableCodeInvalid', { tableCode: table.code }, { tableCode: table.code }));
    else if (tableCodes.has(lower)) out.push(problem('tableDuplicate', { tableCode: table.code }, { tableCode: table.code }));
    else if (!registryCodeOk(registryCode)) {
      out.push(problem('registryCodeInvalid', { registryCode }, { tableCode: table.code }));
    } else if (!hasText(table.nameL10n)) out.push(problem('nameRequired', { item: table.code }, { tableCode: table.code }));
    tableCodes.add(lower);
  }

  for (const table of draft.tables) {
    const seen = new Set<string>();
    for (const column of table.columns) {
      const found = columnProblem(options.typeCode, table, column, seen, tableCodes);
      if (found !== null) out.push(found);
    }
    if (!table.columns.some((column) => column.key)) {
      out.push(problem('tableWithoutKey', { tableCode: table.code }, { tableCode: table.code }));
    }
    out.push(...ruleProblems(table.rules));
  }
  out.push(...ruleProblems(draft.rules));

  const cycle = cycleProblem(draft.tables);
  if (cycle !== null) out.push(cycle);
  if (options.published !== null && options.published !== undefined) {
    out.push(...publishedProblems(draft, options.published));
  }
  return out;
}

/** Додаткова вимога публікації: хоч одна таблиця. */
export function validateForPublish(draft: TypeDraft): DraftProblem[] {
  return draft.tables.length === 0 ? [problem('noTables')] : [];
}
