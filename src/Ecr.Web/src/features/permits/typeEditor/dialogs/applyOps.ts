import type { PermitType, PermitTypeColumn, PermitTypeTable } from '@/features/permits/api/types';

/*
 * Як результат діалогу лягає в тип (Permits, PM-08c). Чисті функції: приймають визначення й
 * повертають НОВЕ; прапорців чернетки (`hasUnpublishedChanges`) і токена не торкаються — це
 * справа сторінки редактора, що тримає чернетку.
 */

/** Додає таблицю в кінець складу. */
export function withTable(type: PermitType, table: PermitTypeTable): PermitType {
  return { ...type, tables: [...type.tables, { ...table, order: type.tables.length + 1 }] };
}

/**
 * Додає стовпці в кінець таблиці, перенумеровуючи `order`.
 *
 * ⛔ Таблиці, якої немає, — виняток, а не мовчазне «нічого»: діалог, що не додав стовпця й
 * не сказав про це, гірший за той, що впав.
 */
export function withColumns(
  type: PermitType,
  tableCode: string,
  columns: readonly PermitTypeColumn[],
): PermitType {
  const target = type.tables.find((table) => table.code === tableCode);
  if (target === undefined) throw new Error(`Таблиці "${tableCode}" немає в типі "${type.code}".`);

  const next = [...target.columns, ...columns].map((column, index) => ({ ...column, order: index + 1 }));

  return {
    ...type,
    tables: type.tables.map((table) => (table.code === tableCode ? { ...table, columns: next } : table)),
  };
}
