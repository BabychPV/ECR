import type { TemplateColumnDto, TemplateStructureDto } from '@/api/types';
import { draftOfFormula, emptyFormulaDraft, type FormulaDraft } from '@/features/templates/formula';

/** Аркуш, таблиця й рядок структури версії — як їх віддає `GET …/structure`. */
export type TemplateSheet = TemplateStructureDto['sheets'][number];
export type TemplateTable = TemplateSheet['tables'][number];
export type TemplateRow = TemplateTable['rows'][number];

/**
 * Чернетка формули для колонки чи рядка.
 *
 * ⛔ Дефект 2026-09-23: раніше тут завжди був `emptyFormulaDraft` — повторне
 * відкриття на колонці (рядку) зі збереженою формулою показувало порожній
 * редактор, хоча PUT зберігав вираз (структура тепер несе його — `GET …/structure`).
 */
export function columnFormulaDraft(tableId: number, column: TemplateColumnDto): FormulaDraft {
  return column.formulaExpression !== null
    ? draftOfFormula(tableId, 'Column', String(column.id), {
        dialect: column.formulaDialect ?? 'Template',
        expression: column.formulaExpression,
      })
    : emptyFormulaDraft(tableId, 'Column', String(column.id));
}

export function rowFormulaDraft(tableId: number, row: TemplateRow): FormulaDraft {
  return row.formulaExpression !== null
    ? draftOfFormula(tableId, 'Row', row.rowKey, {
        dialect: row.formulaDialect ?? 'Template',
        expression: row.formulaExpression,
      })
    : emptyFormulaDraft(tableId, 'Row', row.rowKey);
}

/**
 * Що вибрано в дереві конструктора (`UI-36`, макет `screens-templates.js`: `?node=`).
 *
 * ⚠ Адреса, а не стан компонента: посилання на таблицю відкривається одразу на
 * ній, «Назад» браузера повертає попередній вузол. `UnsavedGuard` блокує лише
 * зміну шляху, тож перемикання вузлів (пошуковий рядок) його не зачіпає.
 */
export type CtorNode =
  | { readonly kind: 'root' }
  | { readonly kind: 'sheet'; readonly code: string }
  | { readonly kind: 'table'; readonly id: number };

export const CtorTabs = ['columns', 'rows', 'formulas', 'rules', 'preview'] as const;
export type CtorTab = (typeof CtorTabs)[number];

export function nodeParam(node: CtorNode): string {
  switch (node.kind) {
    case 'root':
      return 'root';
    case 'sheet':
      return `s:${node.code}`;
    case 'table':
      return `t:${String(node.id)}`;
  }
}

export function sortedSheets(structure: TemplateStructureDto): TemplateSheet[] {
  return [...structure.sheets].sort((a, b) => a.ordinal - b.ordinal);
}

export function sortedTables(sheet: TemplateSheet): TemplateTable[] {
  return [...sheet.tables].sort((a, b) => a.ordinal - b.ordinal);
}

/** Знайдена таблиця разом з аркушем і номером «аркуш.таблиця» (як у макеті: `1.3`). */
export interface LocatedTable {
  readonly sheet: TemplateSheet;
  readonly sheetNo: number;
  readonly table: TemplateTable;
  readonly no: string;
}

export function locateTable(structure: TemplateStructureDto, id: number): LocatedTable | undefined {
  const sheets = sortedSheets(structure);
  for (let s = 0; s < sheets.length; s++) {
    const sheet = sheets[s]!;
    const tables = sortedTables(sheet);
    const index = tables.findIndex((table) => table.id === id);
    if (index >= 0) return { sheet, sheetNo: s + 1, table: tables[index]!, no: `${String(s + 1)}.${String(index + 1)}` };
  }

  return undefined;
}

/**
 * Вузол з адреси. Невідомий чи зниклий (таблицю щойно видалили) — вузол за
 * замовчуванням: перша таблиця першого аркуша, як у макеті; немає таблиць —
 * корінь версії. Порожня сторінка на місці зниклого вузла була б глухим кутом.
 */
export function parseNode(raw: string | null, structure: TemplateStructureDto): CtorNode {
  if (raw === 'root') return { kind: 'root' };

  if (raw?.startsWith('s:') === true) {
    const code = raw.slice(2);
    if (structure.sheets.some((sheet) => sheet.code === code)) return { kind: 'sheet', code };
  }

  if (raw?.startsWith('t:') === true) {
    const id = Number(raw.slice(2));
    if (Number.isInteger(id) && locateTable(structure, id) !== undefined) return { kind: 'table', id };
  }

  for (const sheet of sortedSheets(structure)) {
    const first = sortedTables(sheet)[0];
    if (first !== undefined) return { kind: 'table', id: first.id };
  }

  return { kind: 'root' };
}

/** Вкладка таблиці з адреси; «Rows» динамічної таблиці законна — там пояснення, чому рядків немає. */
export function parseTab(raw: string | null): CtorTab {
  return (CtorTabs as readonly string[]).includes(raw ?? '') ? (raw as CtorTab) : 'columns';
}

/**
 * Дерево з фільтром «Find a table»: збіг за назвою, кодом чи номером таблиці;
 * збіг за назвою чи кодом АРКУША показує всі його таблиці.
 */
export function filterTree(
  structure: TemplateStructureDto,
  query: string,
  nameOf: (table: TemplateTable) => string,
  sheetNameOf: (sheet: TemplateSheet) => string,
): { readonly sheet: TemplateSheet; readonly sheetNo: number; readonly tables: readonly { table: TemplateTable; no: string }[] }[] {
  const q = query.trim().toLowerCase();

  return sortedSheets(structure).flatMap((sheet, s) => {
    const tables = sortedTables(sheet).map((table, i) => ({ table, no: `${String(s + 1)}.${String(i + 1)}` }));
    if (q === '') return [{ sheet, sheetNo: s + 1, tables }];

    const sheetHit = sheetNameOf(sheet).toLowerCase().includes(q) || sheet.code.toLowerCase().includes(q);
    const hits = sheetHit
      ? tables
      : tables.filter(
          ({ table, no }) =>
            nameOf(table).toLowerCase().includes(q) || table.code.toLowerCase().includes(q) || no.startsWith(q),
        );

    return hits.length > 0 ? [{ sheet, sheetNo: s + 1, tables: hits }] : [];
  });
}

/** Ціль формули: обчислювана колонка чи рядок таблиці (вкладка «Formulas»). */
export type FormulaTarget =
  | { readonly kind: 'Column'; readonly column: TemplateColumnDto; readonly expression: string; readonly dialect: string }
  | { readonly kind: 'Row'; readonly row: TemplateRow; readonly expression: string; readonly dialect: string };

/**
 * Наявні формули таблиці — лише ті, що віддала структура (`formulaExpression`).
 * ⚠ Стану «перевірено/помилка» з макета тут немає: структура його не несе
 * (компіляція — при записі й публікації); вигадувати «Valid» не можна.
 */
export function formulasOf(table: TemplateTable): FormulaTarget[] {
  const columns = [...table.columns]
    .sort((a, b) => a.ordinal - b.ordinal)
    .flatMap((column): FormulaTarget[] =>
      column.formulaExpression === null
        ? []
        : [{ kind: 'Column', column, expression: column.formulaExpression, dialect: column.formulaDialect ?? 'Template' }],
    );
  const rows = [...table.rows]
    .sort((a, b) => a.ordinal - b.ordinal)
    .flatMap((row): FormulaTarget[] =>
      row.formulaExpression === null
        ? []
        : [{ kind: 'Row', row, expression: row.formulaExpression, dialect: row.formulaDialect ?? 'Template' }],
    );

  return [...columns, ...rows];
}
