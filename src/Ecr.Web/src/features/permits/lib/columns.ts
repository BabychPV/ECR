/**
 * Спільні типи таблиць перміта (PM-06). Мокап: `docs/design/permits/screens-permits.js`
 * (`col()`, `TYPES_LIST`). Чисті типи без залежностей: їх читають і допоміжні функції в `lib/`,
 * і компоненти в `table/`, і (далі) форми й майстри.
 */

/** Тип стовпця таблиці перміта — рівно вісім типів конструктора з мокапу. */
export type PermitColumnType =
  | 'text'
  | 'localizedText'
  | 'longText'
  | 'number'
  | 'date'
  | 'bool'
  | 'choice'
  | 'link';

/** Опис одного стовпця. `ref` — довідник/таблиця, з якої бере значення `link`. */
export interface PermitColumn {
  readonly code: string;
  readonly label: string;
  readonly type: PermitColumnType;
  /** Одиниця виміру (для `number`, інколи для тексту: «km»). */
  readonly unit?: string | undefined;
  readonly required?: boolean | undefined;
  /** Стовпець входить у складений ключ рядка (унікальність у межах перміта). */
  readonly key?: boolean | undefined;
  /** Значення читається зі звʼязаного запису (`Substance.Code`) і не вводиться. */
  readonly derived?: boolean | undefined;
  /** Назва групи стовпців (згортання широкої таблиці). */
  readonly group?: string | undefined;
  /** Для `link`: `Substances` (спільний реєстр) або `StoragePlaces (this permit)` (таблиця перміта). */
  readonly ref?: string | undefined;
  /** Для `choice`: значення списку, що належить лише цьому типу перміта. */
  readonly choices?: readonly string[] | undefined;
}

/** Значення комірки: число, рядок, так/ні, дата-рядок ISO або порожньо. */
export type PermitCellValue = string | number | boolean | null | undefined;

/** Рядок таблиці перміта: `id` + значення за кодами стовпців. */
export interface PermitRow {
  readonly id: string;
  readonly [code: string]: PermitCellValue;
}

/** Порожнє значення комірки: `null`, `undefined` або рядок із самих пробілів. */
export function isEmptyCell(value: PermitCellValue): boolean {
  return value === null || value === undefined || (typeof value === 'string' && value.trim() === '');
}
