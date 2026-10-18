import type { PermitType, PermitTypeColumn, PermitTypeTable } from '../api/types';
import { ListScopes, isValidCode, type ColumnDataType, type ListScopeKey, type TypeDraft } from './draftTypes';

/*
 * Робоча копія визначення типу дозволу (Permits, PM-08b). ЧИСТА модель: жодного React і мережі.
 *
 * Робоча копія — це ті самі поля, що йдуть у `PUT /api/v1/permit-types/{code}` (назва, категорія,
 * перевірки, таблиці). Шапку дозволу сервер фіксує сам, тож у копії її немає.
 *
 * ⛔ Усі функції незмінні: повертають НОВУ копію (або ту саму, якщо змінювати нічого). Діалоги
 * (PM-08c) викликають їх через `useTypeEditor().apply(...)` і залежать ЛИШЕ від сигнатур цього файлу.
 *
 * Інваріанти, які тримає кожна функція запису (їх же перевіряють тести з мутаційним доказом):
 *  - ключовий стовпець завжди обовʼязковий (`normalizeColumn`);
 *  - код таблиці й код стовпця в межах батька унікальні без огляду на регістр (як на сервері);
 *  - `order` — позиція в масиві, без прогалин (`renumber`);
 *  - налаштування списку відповідають області (`withListScope`), точність — лише для Decimal.
 */

/** Помилка програміста: функція запису викликана з тим, що валідатор мав би зупинити раніше. */
export class DraftModelError extends Error {
  constructor(
    readonly reason: 'tableExists' | 'tableMissing' | 'columnExists' | 'columnMissing' | 'codeInvalid',
    message: string,
  ) {
    super(message);
    this.name = 'DraftModelError';
  }
}

/** Робоча копія з відповіді сервера. */
export function draftOf(type: Pick<PermitType, 'nameL10n' | 'category' | 'rules' | 'tables'>): TypeDraft {
  return {
    nameL10n: type.nameL10n,
    category: type.category,
    rules: type.rules,
    tables: type.tables.map((table) => ({ ...table, columns: table.columns.map(normalizeColumn) })),
  };
}

const sameCode = (a: string, b: string): boolean => a.toLowerCase() === b.toLowerCase();

/** Таблиця за кодом. */
export function findTable(draft: TypeDraft, tableCode: string): PermitTypeTable | undefined {
  return draft.tables.find((table) => sameCode(table.code, tableCode));
}

/** Стовпець за кодом. */
export function findColumn(table: PermitTypeTable, columnCode: string): PermitTypeColumn | undefined {
  return table.columns.find((column) => sameCode(column.code, columnCode));
}

/**
 * Приводить стовпець до дозволеного сервером вигляду: ключ ⇒ обовʼязковий; точність — лише в
 * Decimal; налаштування списку — лише в Lookup і лише ті, що належать області.
 */
export function normalizeColumn(column: PermitTypeColumn): PermitTypeColumn {
  const lookup = column.dataType === 'Lookup';
  const scope = lookup ? column.listScope : null;
  const decimal = column.dataType === 'Decimal';

  return {
    ...column,
    required: column.key ? true : column.required,
    unitId: column.dataType === 'Int' || decimal ? column.unitId : null,
    group: column.group === null || column.group === '' ? null : column.group,
    precision: decimal ? column.precision : null,
    scale: decimal && column.precision !== null ? column.scale : null,
    listScope: scope,
    listRegistryCode: scope === ListScopes.shared ? column.listRegistryCode : null,
    siblingTableCode: scope === ListScopes.permit ? column.siblingTableCode : null,
    choices: scope === ListScopes.type ? column.choices : null,
  };
}

function renumberColumns(columns: readonly PermitTypeColumn[]): PermitTypeColumn[] {
  return columns.map((column, index) => ({ ...normalizeColumn(column), order: index + 1 }));
}

function renumberTables(tables: readonly PermitTypeTable[]): PermitTypeTable[] {
  return tables.map((table, index) => ({ ...table, order: index + 1 }));
}

function mapTable(
  draft: TypeDraft,
  tableCode: string,
  change: (table: PermitTypeTable) => PermitTypeTable,
): TypeDraft {
  if (findTable(draft, tableCode) === undefined) {
    throw new DraftModelError('tableMissing', `Таблиці «${tableCode}» немає в чернетці.`);
  }
  return {
    ...draft,
    tables: draft.tables.map((table) => (sameCode(table.code, tableCode) ? change(table) : table)),
  };
}

/** Заготовка нової таблиці (майстер «Add table…»). */
export interface NewTable {
  readonly code: string;
  readonly nameL10n: Readonly<Record<string, string>>;
  readonly columns: readonly PermitTypeColumn[];
}

/** Додає таблицю в кінець. Код зайнятий чи недопустимий — `DraftModelError`. */
export function addTable(draft: TypeDraft, table: NewTable): TypeDraft {
  if (!isValidCode(table.code)) {
    throw new DraftModelError('codeInvalid', `Код таблиці «${table.code}» недопустимий.`);
  }
  if (findTable(draft, table.code) !== undefined) {
    throw new DraftModelError('tableExists', `Таблиця «${table.code}» уже є.`);
  }
  const added: PermitTypeTable = {
    code: table.code,
    nameL10n: table.nameL10n,
    order: draft.tables.length + 1,
    columns: renumberColumns(table.columns),
    rules: [],
  };
  return { ...draft, tables: renumberTables([...draft.tables, added]) };
}

/** Видаляє таблицю разом зі стовпцями й перевірками. Немає такої — `DraftModelError`. */
export function removeTable(draft: TypeDraft, tableCode: string): TypeDraft {
  mapTable(draft, tableCode, (table) => table);
  return { ...draft, tables: renumberTables(draft.tables.filter((table) => !sameCode(table.code, tableCode))) };
}

/** Змінює назву таблиці (код не міняється). */
export function renameTable(draft: TypeDraft, tableCode: string, nameL10n: Readonly<Record<string, string>>): TypeDraft {
  return mapTable(draft, tableCode, (table) => ({ ...table, nameL10n }));
}

/** Додає один стовпець у кінець таблиці. */
export function addColumn(draft: TypeDraft, tableCode: string, column: PermitTypeColumn): TypeDraft {
  return appendColumns(draft, tableCode, [column]);
}

/** Додає кілька стовпців у кінець таблиці одним кроком («Add months January–December…»). */
export function appendColumns(draft: TypeDraft, tableCode: string, columns: readonly PermitTypeColumn[]): TypeDraft {
  return mapTable(draft, tableCode, (table) => {
    const taken = new Set(table.columns.map((column) => column.code.toLowerCase()));
    for (const column of columns) {
      if (!isValidCode(column.code)) {
        throw new DraftModelError('codeInvalid', `Код стовпця «${column.code}» недопустимий.`);
      }
      if (taken.has(column.code.toLowerCase())) {
        throw new DraftModelError('columnExists', `Стовпець «${column.code}» уже є в «${table.code}».`);
      }
      taken.add(column.code.toLowerCase());
    }
    return { ...table, columns: renumberColumns([...table.columns, ...columns]) };
  });
}

/** Змінює поля стовпця. Код не міняється (після публікації він незмінний). */
export function updateColumn(
  draft: TypeDraft,
  tableCode: string,
  columnCode: string,
  patch: Partial<Omit<PermitTypeColumn, 'code' | 'order'>>,
): TypeDraft {
  return mapTable(draft, tableCode, (table) => {
    if (findColumn(table, columnCode) === undefined) {
      throw new DraftModelError('columnMissing', `Стовпця «${columnCode}» немає в «${tableCode}».`);
    }
    return {
      ...table,
      columns: table.columns.map((column) =>
        sameCode(column.code, columnCode)
          ? normalizeColumn({ ...column, ...patch, code: column.code, order: column.order })
          : column,
      ),
    };
  });
}

/** Видаляє стовпець. */
export function removeColumn(draft: TypeDraft, tableCode: string, columnCode: string): TypeDraft {
  return mapTable(draft, tableCode, (table) => {
    if (findColumn(table, columnCode) === undefined) {
      throw new DraftModelError('columnMissing', `Стовпця «${columnCode}» немає в «${tableCode}».`);
    }
    return { ...table, columns: renumberColumns(table.columns.filter((column) => !sameCode(column.code, columnCode))) };
  });
}

/** Переносить стовпець на позицію `toIndex` (з нуля, за межі масиву обрізається). */
export function moveColumn(draft: TypeDraft, tableCode: string, columnCode: string, toIndex: number): TypeDraft {
  return mapTable(draft, tableCode, (table) => {
    const from = table.columns.findIndex((column) => sameCode(column.code, columnCode));
    if (from < 0) throw new DraftModelError('columnMissing', `Стовпця «${columnCode}» немає в «${tableCode}».`);
    const target = Math.max(0, Math.min(table.columns.length - 1, toIndex));
    const next = [...table.columns];
    const [moved] = next.splice(from, 1);
    next.splice(target, 0, moved!);
    return { ...table, columns: renumberColumns(next) };
  });
}

/** Нова область списку: чистить поля, що їй не належать, і ставить типові. */
export function withListScope(
  column: PermitTypeColumn,
  scope: ListScopeKey,
  siblingTables: readonly string[] = [],
): PermitTypeColumn {
  const base = { ...column, dataType: 'Lookup', listScope: ListScopes[scope] };
  switch (scope) {
    case 'shared':
      return normalizeColumn({ ...base, listRegistryCode: column.listRegistryCode, siblingTableCode: null, choices: null });
    case 'type':
      return normalizeColumn({ ...base, listRegistryCode: null, siblingTableCode: null, choices: column.choices ?? [] });
    case 'permit':
      return normalizeColumn({
        ...base,
        listRegistryCode: null,
        siblingTableCode: column.siblingTableCode ?? siblingTables[0] ?? null,
        choices: null,
      });
  }
}

/** Область списку стовпця або `null`, якщо це не посилання. */
export function scopeKeyOf(column: Pick<PermitTypeColumn, 'dataType' | 'listScope'>): ListScopeKey | null {
  if (column.dataType !== 'Lookup') return null;
  const found = (Object.keys(ListScopes) as ListScopeKey[]).find((key) => ListScopes[key] === column.listScope);
  return found ?? null;
}

/** Стовпці інших таблиць, що беруть значення з цієї (для діалогу видалення таблиці). */
export function referencesTo(draft: TypeDraft, tableCode: string): { tableCode: string; columnCode: string }[] {
  return draft.tables.flatMap((table) =>
    table.columns
      .filter((column) => column.siblingTableCode !== null && sameCode(column.siblingTableCode, tableCode))
      .map((column) => ({ tableCode: table.code, columnCode: column.code })),
  );
}

/** Ключові стовпці таблиці у порядку показу. */
export function keyColumns(table: PermitTypeTable): PermitTypeColumn[] {
  return table.columns.filter((column) => column.key);
}

/**
 * Назви ключових стовпців для підказки «Unique rows: Permit + A + B». Порожній масив — ключа ще
 * немає. Назва — вже вибрана мовою зразка (`labelOf`).
 */
export function uniqueKeyParts(table: PermitTypeTable, labelOf: (column: PermitTypeColumn) => string): string[] {
  return keyColumns(table).map(labelOf);
}

/** Вільний код: `base`, а далі `base_2`, `base_3`… Недопустимі символи стають підкресленням. */
export function freeCode(base: string, taken: Iterable<string>): string {
  const used = new Set([...taken].map((code) => code.toLowerCase()));
  let stem = base.replace(/[^A-Za-z0-9]+/g, '_').replace(/^_+|_+$/g, '');
  if (stem === '') stem = 'Item';
  if (!/^[A-Za-z]/.test(stem)) stem = `C_${stem}`;
  stem = stem.slice(0, 60);
  if (!used.has(stem.toLowerCase())) return stem;
  for (let n = 2; ; n += 1) {
    const candidate = `${stem}_${n}`;
    if (!used.has(candidate.toLowerCase())) return candidate;
  }
}

/** Стовпець за замовчуванням для нової таблиці й діалогу «Add column». */
export function newColumn(code: string, nameL10n: Readonly<Record<string, string>>, dataType: ColumnDataType = 'String'): PermitTypeColumn {
  return normalizeColumn({
    code,
    nameL10n,
    dataType,
    unitId: null,
    required: false,
    key: false,
    group: null,
    precision: null,
    scale: null,
    listScope: dataType === 'Lookup' ? ListScopes.shared : null,
    listRegistryCode: null,
    siblingTableCode: null,
    choices: null,
    order: 0,
  });
}

export { ColumnDataTypes, ListScopes, ReservedColumnCode, isValidCode } from './draftTypes';
export type { ColumnDataType, ListScopeKey, TypeDraft } from './draftTypes';
export { countChanges, sameDraft } from './draftDiff';
export { validateDraft, validateForPublish } from './draftValidate';
export type { DraftProblem } from './draftValidate';
