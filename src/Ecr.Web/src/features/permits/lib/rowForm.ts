// Споживач: PM-07 (сторінки пермітів: рядок-форма, вставка з Excel, широка таблиця).
import { isEmptyCell, type PermitCellValue, type PermitColumn } from './columns';

/**
 * Перевірка форми рядка таблиці перміта (PM-06). Дзеркало `save()` форми рядка мокапу
 * (`docs/design/permits/screens-permits.js`): спершу обовʼязкові, потім дубль складеного ключа.
 * Сервер перевіряє те саме; ця перевірка дає відповідь до відправки.
 */

export type RowValues = Readonly<Record<string, PermitCellValue>>;

export type RowFormErrors =
  | { readonly ok: true }
  | { readonly ok: false; readonly kind: 'required'; readonly missing: readonly string[] }
  | { readonly ok: false; readonly kind: 'duplicate'; readonly keyColumns: readonly string[] };

/**
 * Обовʼязкові стовпці, що введені людиною, а не похідні: `derived` читається зі звʼязаного запису.
 */
export function requiredColumns(columns: readonly PermitColumn[]): PermitColumn[] {
  return columns.filter((column) => column.required === true && column.derived !== true);
}

/** Стовпці складеного ключа рядка. */
export function keyColumns(columns: readonly PermitColumn[]): PermitColumn[] {
  return columns.filter((column) => column.key === true);
}

/**
 * Значення ключа для порівняння: `null`/порожнє → `''`, інакше `String(...)` без крайніх пробілів.
 * ⚠ Мокап писав `String(v || '')`, і число `0` ставало `''` — два рядки з ключем `0` і порожнім
 * ключем вважалися б однаковими. Тут `0` лишається `'0'`, а `false` — `'false'`.
 */
export function keyPart(value: PermitCellValue): string {
  return isEmptyCell(value) ? '' : String(value).trim();
}

function sameKey(keys: readonly PermitColumn[], a: RowValues, b: RowValues): boolean {
  return keys.every((key) => keyPart(a[key.code]) === keyPart(b[key.code]));
}

/**
 * Перевіряє введені значення рядка.
 *
 * @param existing рядки таблиці цього перміта, що вже збережені
 * @param editing рядок, що редагується (посилання з `existing`): сам із собою не конфліктує
 *
 * Обовʼязкові перевіряються першими й обривають перевірку (як у мокапі): дубль за ключем, у якому
 * ще не заповнені ключові поля, нічого не каже людині.
 */
export function validateRow(
  columns: readonly PermitColumn[],
  values: RowValues,
  existing: readonly RowValues[] = [],
  editing?: RowValues | undefined,
): RowFormErrors {
  const missing = requiredColumns(columns)
    .filter((column) => isEmptyCell(values[column.code]))
    .map((column) => column.code);
  if (missing.length > 0) return { ok: false, kind: 'required', missing };

  const keys = keyColumns(columns);
  if (keys.length > 0 && existing.some((row) => row !== editing && sameKey(keys, row, values))) {
    return { ok: false, kind: 'duplicate', keyColumns: keys.map((key) => key.code) };
  }

  return { ok: true };
}
