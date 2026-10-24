import { localName } from './names';
import type { RegistryBatchItem, RegistryRow } from '@/features/registries/rows/api';
import type { PermitTypeColumn, PermitTypeTable } from '../api/types';
import { type PermitCellValue, type PermitColumn, type PermitColumnType, type PermitRow } from '../lib/columns';
import { parseLocaleDate, parseLocaleNumber } from '../lib/pasteRows';
import { optionsFor, scopeOf, type PickerSources } from '../lib/pickerScope';
import type { RowValues } from '../lib/rowForm';

/**
 * Модель екрана дозволу (PM-07): сервер описує стовпці типу (`PermitTypeColumn`), а клієнтські модулі PM-06
 * (`PermitDefTable`, `rowForm`, `pickerScope`, `pasteRows`) говорять мовою `PermitColumn`. Тут — переклад в обидва
 * боки; жодних запитів і React.
 *
 * ⚠ Рядки таблиць пишуться наявним `entries/batch` довідника `PRM_…`: поле композиції — `PERMIT` (id дозволу),
 * значення ідуть РЯДКАМИ (число — інваріантно, дата — `yyyy-MM-dd`, логічне — `true`/`false`, `Lookup` — id запису).
 */
export const PermitParentField = 'PERMIT';

/** Стовпець таблиці: клієнтський опис і серверне джерело (точність, область списку). */
export interface PermitField {
  readonly column: PermitColumn;
  readonly source: PermitTypeColumn;
}

/** Рядок із сервера: клієнтський рядок, токен конкуренції, id записів-цілей для `Lookup` і сирі значення. */
export interface LoadedRow {
  readonly row: PermitRow;
  readonly version: string;
  readonly lookupIds: Readonly<Record<string, string>>;
  readonly raw: Readonly<Record<string, string>>;
}

/** Значення форми рядка: усе рядками, як у полях вводу; `Lookup` — id запису-цілі, `Bool` — `true`/`false`. */
export type FormValues = Readonly<Record<string, string>>;

// Назви — у `names.ts` (без розбору вставки; бюджет маршруту, D-132); тут — реекспорт для наявних імпортів.
export { localName, unitLabels } from './names';

function columnType(source: PermitTypeColumn): PermitColumnType {
  switch (source.dataType) {
    case 'Int':
    case 'Decimal':
      return 'number';
    case 'Bool':
      return 'bool';
    case 'Date':
      return 'date';
    case 'Lookup':
      return source.listScope === 'TypeOnly' ? 'choice' : 'link';
    default:
      return 'text';
  }
}

function refOf(source: PermitTypeColumn): string | undefined {
  if (source.dataType !== 'Lookup') return undefined;
  if (source.listScope === 'SamePermitTable') {
    return source.siblingTableCode == null ? undefined : `${source.siblingTableCode} (this permit)`;
  }
  return source.listScope === 'SharedRegistry' ? (source.listRegistryCode ?? undefined) : undefined;
}

/** Стовпці таблиці типу за порядком. */
export function fieldsOf(table: PermitTypeTable, units: ReadonlyMap<number, string>): PermitField[] {
  return [...table.columns]
    .sort((a, b) => a.order - b.order)
    .map((source) => {
      const unit = source.unitId == null ? undefined : units.get(source.unitId);
      const ref = refOf(source);
      const column: PermitColumn = {
        code: source.code,
        label: localName(source.nameL10n) || source.code,
        type: columnType(source),
        unit,
        required: source.required,
        key: source.key,
        group: source.group ?? undefined,
        ref,
        choices: source.listScope === 'TypeOnly' ? (source.choices ?? []) : undefined,
      };
      return { column, source };
    });
}

/** Чи стовпець — посилання на запис (`Lookup`): його значення в пакеті — id цілі. */
export function isLookup(column: PermitColumn): boolean {
  return column.type === 'choice' || column.type === 'link';
}

function cellOf(column: PermitColumn, raw: string, display: string | null): PermitCellValue {
  if (raw === '') return null;
  switch (column.type) {
    case 'number': {
      const value = Number(raw);
      return Number.isFinite(value) ? value : null;
    }
    case 'bool':
      return raw === 'true';
    case 'date':
      return raw.slice(0, 10);
    case 'choice':
    case 'link':
      return display != null && display !== '' ? display : raw;
    default:
      return raw;
  }
}

/** Рядок сервера → рядок таблиці. */
export function loadRow(fields: readonly PermitField[], source: RegistryRow): LoadedRow {
  const values: Record<string, PermitCellValue> = {};
  const lookupIds: Record<string, string> = {};
  const raw: Record<string, string> = {};

  for (const { column } of fields) {
    const entry = source.values[column.code];
    const text = entry?.value ?? '';
    raw[column.code] = text;
    if (isLookup(column) && text !== '') lookupIds[column.code] = text;
    values[column.code] = cellOf(column, text, entry?.display ?? null);
  }

  return { row: { id: String(source.id), ...values }, version: source.version, lookupIds, raw };
}

/** Початкові значення форми: нового рядка — порожні (`Bool` — `false`), наявного — з сервера. */
export function initialForm(fields: readonly PermitField[], loaded?: LoadedRow): FormValues {
  const form: Record<string, string> = {};
  for (const { column } of fields) {
    const raw = loaded?.raw[column.code] ?? '';
    form[column.code] = column.type === 'bool' ? (raw === 'true' ? 'true' : 'false') : column.type === 'date' ? raw.slice(0, 10) : raw;
  }
  return form;
}

/** Помилка поля форми: що саме не так (текст — з каталогу, вирішує форма). */
export type FieldProblem = 'notNumber' | 'notInteger' | 'notDate';

/** Чи значення стовпця у формі прочитується; порожнє — завжди так (обов'язковість рахує `validateRow`). */
export function fieldProblem(field: PermitField, text: string): FieldProblem | null {
  const value = text.trim();
  if (value === '') return null;

  if (field.column.type === 'number') {
    const parsed = parseLocaleNumber(value);
    if (parsed === null) return 'notNumber';
    return field.source.dataType === 'Int' && !Number.isInteger(parsed) ? 'notInteger' : null;
  }
  if (field.column.type === 'date') return parseLocaleDate(value) === null ? 'notDate' : null;
  return null;
}

/** Значення форми → значення для `validateRow` (число розібране, `Lookup` — підпис за id). */
export function toRowValues(
  fields: readonly PermitField[],
  form: FormValues,
  labelOfId: (column: PermitColumn, id: string) => string | null,
): RowValues {
  const values: Record<string, PermitCellValue> = {};
  for (const { column } of fields) {
    const text = (form[column.code] ?? '').trim();
    if (text === '') {
      values[column.code] = null;
    } else if (column.type === 'number') {
      values[column.code] = parseLocaleNumber(text);
    } else if (column.type === 'bool') {
      values[column.code] = text === 'true';
    } else if (isLookup(column)) {
      values[column.code] = labelOfId(column, text) ?? text;
    } else if (column.type === 'date') {
      values[column.code] = parseLocaleDate(text) ?? text;
    } else {
      values[column.code] = text;
    }
  }
  return values;
}

/** Запис значення форми для пакета: число — інваріантно, дата — ISO, решта як є. */
export function wireValue(field: PermitField, text: string): string {
  const value = text.trim();
  if (value === '') return '';
  if (field.column.type === 'number') return String(parseLocaleNumber(value) ?? value);
  if (field.column.type === 'date') return parseLocaleDate(value) ?? value;
  return value;
}

/** Пакет для нового рядка: порожні значення не надсилаються, поле композиції — id дозволу; код присвоює сервер. */
export function newRowItem(
  clientRowId: string,
  permitId: number,
  fields: readonly PermitField[],
  form: FormValues,
): RegistryBatchItem {
  const values: Record<string, string> = { [PermitParentField]: String(permitId) };
  for (const field of fields) {
    const sent = wireValue(field, form[field.column.code] ?? '');
    if (sent !== '') values[field.column.code] = sent;
  }
  return { clientRowId, op: 'upsert', id: null, code: null, baseVersion: null, values };
}

/**
 * Пакет для наявного рядка: лише змінені поля, очищене — `null`; `baseVersion` — токен, з яким рядок відкрили (чужу
 * зміну сервер назве `entryChanged`). Нічого не змінено — `null`.
 */
export function updateRowItem(
  clientRowId: string,
  loaded: LoadedRow,
  fields: readonly PermitField[],
  form: FormValues,
  original: FormValues,
): RegistryBatchItem | null {
  const values: Record<string, string | null> = {};
  for (const field of fields) {
    const code = field.column.code;
    const now = wireValue(field, form[code] ?? '');
    const before = wireValue(field, original[code] ?? '');
    if (now !== before) values[code] = now === '' ? null : now;
  }
  if (Object.keys(values).length === 0) return null;
  return { clientRowId, op: 'upsert', id: Number(loaded.row.id), code: null, baseVersion: loaded.version, values };
}

/** Пакет видалення рядка. */
export function deleteRowItem(clientRowId: string, loaded: LoadedRow): RegistryBatchItem {
  return { clientRowId, op: 'delete', id: Number(loaded.row.id), code: null, baseVersion: loaded.version, values: null };
}

/** Підсумок головного стовпця для рядка «Σ …» (сервер віддає `total` рядком). */
export function totalSums(totalColumnCode: string | null, total: string | null): Readonly<Record<string, number>> {
  if (totalColumnCode === null || total === null) return {};
  const value = Number(total);
  return Number.isFinite(value) ? { [totalColumnCode]: value } : {};
}

/** Значення пікерів: підписи для `optionsFor` і id записів за підписом. */
export interface PickerData {
  readonly sources: PickerSources;
  /** `код реєстру → (підпис → id)`; ключ `permit:<код таблиці>` — рядки сестринської таблиці цього дозволу. */
  readonly ids: Readonly<Record<string, ReadonlyMap<string, string>>>;
}

/** Ключ у {@link PickerData.ids} для стовпця-посилання (`null` — не посилання). */
export function pickerKey(column: PermitColumn, source: PermitTypeColumn): string | null {
  if (!isLookup(column)) return null;
  if (scopeOf(column) === 'permit') return source.siblingTableCode == null ? null : `permit:${source.siblingTableCode}`;
  return source.listRegistryCode ?? null;
}

/** Варіанти вибору стовпця: підпис + id запису; підпис без id у реєстрі відкидається (його не записати). */
export function pickerOptions(
  field: PermitField,
  data: PickerData,
): { readonly value: string; readonly label: string }[] {
  const key = pickerKey(field.column, field.source);
  const ids = key === null ? undefined : data.ids[key];
  return optionsFor(field.column, data.sources).flatMap((option) => {
    const id = ids?.get(option.label);
    return id === undefined ? [] : [{ value: id, label: option.label }];
  });
}

/** Підпис за id серед варіантів стовпця. */
export function labelOfId(field: PermitField | undefined, data: PickerData, id: string): string | null {
  if (field === undefined) return null;
  return pickerOptions(field, data).find((option) => option.value === id)?.label ?? null;
}
