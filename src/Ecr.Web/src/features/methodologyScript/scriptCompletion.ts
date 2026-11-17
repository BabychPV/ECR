import { ScriptAggregates, ScriptKeywords } from './scriptLanguage';
import type { ScriptOutlineDto } from './types';

/**
 * Підказки редактора скрипта — чиста функція без Monaco (план MS, розд. 6).
 * Обгортка-провайдер у `ScriptEditor.tsx` лише перекладає результат.
 */

export type ScriptCompletionKind =
  | 'keyword'
  | 'snippet'
  | 'field'
  | 'constant'
  | 'variable'
  | 'function'
  | 'property'
  | 'module';

export interface ScriptCompletionItem {
  readonly label: string;
  readonly kind: ScriptCompletionKind;
  readonly insertText: string;
  /** `insertText` — синтаксис сніпетів Monaco (`${1:x}`). */
  readonly isSnippet: boolean;
  readonly detail?: string;
  readonly documentation?: string;
  readonly sortText?: string;
}

export interface ScriptInputColumn {
  readonly code: string;
  readonly header?: string | null;
  readonly dataType: string;
  readonly isMonthColumn: boolean;
}

export interface ScriptInputTable {
  readonly tableCode: string;
  readonly columns: readonly ScriptInputColumn[];
}

export interface ScriptFunctionInfo {
  readonly name: string;
  readonly signature?: string | null;
}

export interface ScriptCompletionContext {
  /** Увесь текст скрипта. */
  readonly text: string;
  /** Зміщення курсора в тексті. */
  readonly offset: number;
  readonly outline: ScriptOutlineDto | null;
  /** Колонки таблиць версії шаблону (наявний API структури). */
  readonly tables: readonly ScriptInputTable[];
  /** Вбудовані функції діалекту методологій. */
  readonly functions: readonly ScriptFunctionInfo[];
  readonly headerFields: readonly string[];
}

/** Члени `period.` (3.5). */
export const PeriodMembers: readonly string[] = [
  'Days', 'Hours', 'Seconds', 'Start', 'End', 'Year', 'Month', 'EndKey',
];

/** Символи, після яких Monaco відкриває підказки. */
export const ScriptTriggerCharacters: readonly string[] = ['.', '['];

const MonthSuffix = /_M(?:0[1-9]|1[0-2])$/;

interface SnippetDef {
  readonly label: string;
  readonly detail: string;
  readonly body: string;
}

const Snippets: readonly SnippetDef[] = [
  {
    label: 'rows from',
    detail: 'rows from … where …;',
    body: 'rows from ${1:Table}\n    where row.${2:Column} == ${3:entry("REGISTRY", "CODE")};',
  },
  {
    label: 'const map',
    detail: 'const Name = { … };',
    body:
      'const ${1:Name} = {\n    ${2:Key1}: { ${3:A}: ${4:0}, ${5:B}: ${6:0} },\n' +
      '    ${7:Key2}: { ${3:A}: ${8:0}, ${5:B}: ${9:0} },\n};',
  },
  {
    label: 'output',
    detail: 'output CODE : unit = expr;',
    body: 'output ${1:CODE} : ${2:unit} = ${3:expr};',
  },
  {
    label: 'visible var',
    detail: 'visible var name : unit = expr;',
    body: 'visible var ${1:name} : ${2:unit} = ${3:expr};',
  },
  {
    label: 'foreach substance',
    detail: 'foreach substance S in ("…") { … }',
    body:
      'foreach substance ${1:S} in ("${2:CODE}") {\n    skip when ${3:condition};\n' +
      '    output ${4:OUT} per ${1:S} : ${5:unit} = ${6:expr};\n}',
  },
  {
    label: 'foreach repeat',
    detail: 'foreach X in (…) { … {X} … }',
    body:
      'foreach ${1:S} in (${2:301}, ${3:304}) {\n' +
      '    output ${4:CODE}_{${1:S}} : ${5:unit} = ${6:expr};\n}',
  },
  {
    label: 'if else',
    detail: 'if (…) { … } else { … }',
    body: 'if (${1:condition}) {\n    $2\n} else {\n    $0\n}',
  },
  {
    label: 'table + aggregate',
    detail: 'table Alias = T; var x = Sum(…)',
    body:
      'table ${1:Alias} = ${2:TableCode};\n' +
      'var ${3:name} = ${4|Sum,Min,Max,Avg,First|}(${1:Alias}.${5:Column} where ${1:Alias}.${6:Other} == ${7:period.Month});',
  },
  {
    label: 'reject when',
    detail: 'reject when condition;',
    body: 'reject when ${1:condition};',
  },
];

const KeywordBodies: Readonly<Record<string, string>> = {
  entry: 'entry("${1:REGISTRY}", "${2:CODE}")',
};

/** Чи курсор усередині рядка або коментаря. */
export function isInsideStringOrComment(text: string, offset: number): boolean {
  let i = 0;
  const end = Math.min(offset, text.length);
  while (i < end) {
    const c = text[i];
    const next = text[i + 1];
    if (c === '/' && next === '/') {
      const nl = text.indexOf('\n', i);
      if (nl === -1 || nl >= end) return true;
      i = nl + 1;
    } else if (c === '/' && next === '*') {
      const close = text.indexOf('*/', i + 2);
      if (close === -1 || close + 2 > end) return true;
      i = close + 2;
    } else if (c === '"' || c === "'") {
      let j = i + 1;
      while (j < end && text[j] !== c) {
        if (text[j] === '\\') j++;
        else if (text[j] === '\n') break;
        j++;
      }
      if (j >= end) return true;
      i = text[j] === c ? j + 1 : j;
    } else {
      i++;
    }
  }
  return false;
}

/** `Qualifier.partial` перед курсором; `qualifier` порожній, якщо ланцюжок `a.b.c`. */
export function qualifierAt(
  text: string,
  offset: number,
): { readonly qualifier: string; readonly partial: string } | null {
  const before = text.slice(0, offset);
  const match = /([A-Za-z_][A-Za-z0-9_]*)\s*\.\s*([A-Za-z0-9_]*)$/.exec(before);
  if (match === null) return null;
  const start = match.index;
  if (start > 0 && text[start - 1] === '.') return { qualifier: '', partial: match[2] ?? '' };
  return { qualifier: match[1] ?? '', partial: match[2] ?? '' };
}

/** Псевдоніми `table Alias = [Tpl.]Code;` → код таблиці. */
export function declaredAliases(
  text: string,
  outline: ScriptOutlineDto | null,
): ReadonlyMap<string, string> {
  const map = new Map<string, string>();
  const rx = /\btable\s+([A-Za-z_]\w*)\s*=\s*(?:[A-Za-z_]\w*\s*\.\s*)?([A-Za-z_]\w*)\s*;/g;
  for (const m of text.matchAll(rx)) {
    if (m[1] !== undefined && m[2] !== undefined) map.set(m[1], m[2]);
  }
  for (const table of outline?.tables ?? []) {
    if (table.alias !== undefined && table.alias !== null && table.alias !== '') {
      map.set(table.alias, table.code);
    }
  }
  return map;
}

function columnItems(
  columns: readonly ScriptInputColumn[],
  tableCode: string,
): ScriptCompletionItem[] {
  const seen = new Set<string>();
  const items: ScriptCompletionItem[] = [];
  for (const column of columns) {
    const monthly = column.isMonthColumn || MonthSuffix.test(column.code);
    const name = monthly ? column.code.replace(MonthSuffix, '') : column.code;
    if (seen.has(name)) continue;
    seen.add(name);
    const header = column.header === undefined || column.header === null ? '' : column.header;
    items.push({
      label: name,
      kind: 'field',
      insertText: monthly ? `${name}[\${1:period.Month}]` : name,
      isSnippet: monthly,
      detail: monthly ? `${column.dataType}, ${tableCode}[m]` : `${column.dataType}, ${tableCode}`,
      ...(header === '' ? {} : { documentation: header }),
    });
  }
  return items;
}

function rowColumns(context: ScriptCompletionContext): ScriptCompletionItem[] {
  const rowTables = new Set(
    (context.outline?.tables ?? [])
      .filter((t) => t.alias === undefined || t.alias === null || t.alias === '')
      .map((t) => t.code),
  );
  const chosen =
    rowTables.size === 0
      ? context.tables
      : context.tables.filter((t) => rowTables.has(t.tableCode));
  const source = chosen.length === 0 ? context.tables : chosen;
  const seen = new Set<string>();
  const items: ScriptCompletionItem[] = [];
  for (const table of source) {
    for (const item of columnItems(table.columns, table.tableCode)) {
      if (seen.has(item.label)) continue;
      seen.add(item.label);
      items.push(item);
    }
  }
  return items;
}

function simple(
  names: readonly string[],
  kind: ScriptCompletionKind,
  detail?: string,
): ScriptCompletionItem[] {
  return names.map((name) => ({
    label: name,
    kind,
    insertText: name,
    isSnippet: false,
    ...(detail === undefined ? {} : { detail }),
  }));
}

function findNames(text: string, rx: RegExp): string[] {
  const out: string[] = [];
  for (const m of text.matchAll(rx)) {
    if (m[1] !== undefined) out.push(m[1]);
  }
  return out;
}

function nameItems(context: ScriptCompletionContext): ScriptCompletionItem[] {
  const before = context.text.slice(0, context.offset);
  const items: ScriptCompletionItem[] = [];
  const seen = new Set<string>();
  const add = (list: ScriptCompletionItem[]): void => {
    for (const item of list) {
      if (seen.has(item.label)) continue;
      seen.add(item.label);
      items.push(item);
    }
  };

  const outline = context.outline;
  add(
    (outline?.constants ?? []).map((c) => ({
      label: c.name,
      kind: 'constant' as const,
      insertText: c.name,
      isSnippet: false,
      detail: `const (${c.kind})`,
    })),
  );
  add(simple(findNames(before, /\bvar\s+([A-Za-z_]\w*)/g), 'variable', 'var'));
  add(
    (outline?.outputs ?? []).map((o) => ({
      label: o.code,
      kind: 'variable' as const,
      insertText: o.code,
      isSnippet: false,
      detail: `output : ${o.unit}`,
    })),
  );
  add(simple(findNames(context.text, /\bfunction\s+([A-Za-z_]\w*)/g), 'function', 'function'));
  add(
    simple(
      findNames(before, /\bforeach\s+(?:substance\s+)?([A-Za-z_]\w*)\s+in\b/g),
      'variable',
      'foreach',
    ),
  );
  add(simple([...declaredAliases(context.text, outline).keys()], 'module', 'table'));
  add(simple(outline?.imports ?? [], 'module', 'import'));
  return items;
}

function qualifiedItems(
  context: ScriptCompletionContext,
  qualifier: string,
): ScriptCompletionItem[] {
  switch (qualifier) {
    case 'row':
      return rowColumns(context);
    case 'period':
      return simple(PeriodMembers, 'property', 'period');
    case 'header':
      return simple(context.headerFields, 'property', 'header');
    case '':
      return [];
    default:
      break;
  }

  const aliasTable = declaredAliases(context.text, context.outline).get(qualifier);
  if (aliasTable !== undefined) {
    const table = context.tables.find((t) => t.tableCode === aliasTable);
    return table === undefined ? [] : columnItems(table.columns, table.tableCode);
  }

  const substanceVars = findNames(context.text, /\bforeach\s+substance\s+([A-Za-z_]\w*)\s+in\b/g);
  if (substanceVars.includes(qualifier)) return simple(['code'], 'property', 'substance');

  return [];
}

/** Підказки в позиції курсора. */
export function scriptCompletions(context: ScriptCompletionContext): ScriptCompletionItem[] {
  if (isInsideStringOrComment(context.text, context.offset)) return [];

  const qualified = qualifierAt(context.text, context.offset);
  if (qualified !== null) return qualifiedItems(context, qualified.qualifier);

  const items: ScriptCompletionItem[] = [];

  for (const keyword of ScriptKeywords) {
    const body = KeywordBodies[keyword];
    items.push({
      label: keyword,
      kind: 'keyword',
      insertText: body ?? keyword,
      isSnippet: body !== undefined,
      sortText: `1_${keyword}`,
    });
  }

  for (const snippet of Snippets) {
    items.push({
      label: snippet.label,
      kind: 'snippet',
      insertText: snippet.body,
      isSnippet: true,
      detail: snippet.detail,
      sortText: `0_${snippet.label}`,
    });
  }

  for (const name of ScriptAggregates) {
    const tableOnly = name === 'Count' || name === 'Any';
    items.push({
      label: name,
      kind: 'function',
      insertText: tableOnly
        ? `${name}(\${1:Alias} where \${2:condition})`
        : `${name}(\${1:Alias.Column} where \${2:condition})`,
      isSnippet: true,
      detail: tableOnly ? `${name}(Alias where cond)` : `${name}(Alias.Column where cond)`,
      sortText: `2_${name}`,
    });
  }

  items.push(...nameItems(context).map((i) => ({ ...i, sortText: `3_${i.label}` })));

  for (const fn of context.functions) {
    if (fn.name.startsWith('$')) continue;
    items.push({
      label: fn.name,
      kind: 'function',
      insertText: `${fn.name}($0)`,
      isSnippet: true,
      ...(fn.signature === undefined || fn.signature === null ? {} : { detail: fn.signature }),
      sortText: `4_${fn.name}`,
    });
  }

  return items;
}
