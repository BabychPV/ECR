import { t } from '@/shared/i18n';
import type { AirAddedColumn, AirLimitsPermit, AirSource, AirSubstance } from './airTypes';

/*
 * Модель картки «Air limits» (NAE-план N3): підпис типу джерела, прочерк для порожнього, пошук. Чиста, без React.
 */

/** Прочерк для порожнього значення (десяткові приходять рядками й показуються як є — без округлення, `t/year` повний). */
export const Dash = '—';

export function cell(value: string | null | undefined): string {
  return value === null || value === undefined || value === '' ? Dash : value;
}

/**
 * Десяткове число з сервера для показу. Реальний сервер віддає рядок із повною шкалою колонки
 * (`0.5000000000000000`, `0.0000000000035000`): нулі в хвості дробу не несуть значення і засмічують таблицю. Відкидаємо лише
 * їх (і крапку, якщо дріб порожній) — ЗНАЧЕННЯ не змінюється й не округлюється (`3.5e-12` лишається `0.0000000000035`).
 * Не десятковий текст показується як є.
 */
export function num(value: string | null | undefined): string {
  const text = cell(value);
  if (!/^-?\d+\.\d+$/.test(text)) return text;

  return text.replace(/0+$/, '').replace(/\.$/, '');
}

/**
 * Підпис типу джерела. Сервер віддає канонічні латинські `T` (точкове) і `P1` (площинне); кириличні `Т`/`П1` з
 * книги нормалізовані на сервері. Невідомий код показується як є — не вгадується.
 */
export function sourceTypeLabel(type: string | null): string {
  switch (type) {
    case 'T':
      return t('permits.air.type.point');
    case 'P1':
      return t('permits.air.type.diffuse');
    case null:
    case '':
      return Dash;
    default:
      return type;
  }
}

/** Скорочений код типу для вузької колонки (`Т` / `П1` кирилицею, як у книзі). */
export function sourceTypeShort(type: string | null): string {
  switch (type) {
    case 'T':
      return t('permits.air.type.pointShort');
    case 'P1':
      return t('permits.air.type.diffuseShort');
    default:
      return cell(type);
  }
}

function folded(value: string | null | undefined): string {
  return (value ?? '').toLocaleLowerCase();
}

function substanceMatches(substance: AirSubstance, needle: string): boolean {
  return folded(substance.substanceCode).includes(needle) || folded(substance.substanceName).includes(needle);
}

/** Чи відповідає джерело рядку пошуку: код НАЕ, назва, точка викиду, майданчик чи код/назва речовини. */
export function sourceMatches(source: AirSource, query: string): boolean {
  const needle = folded(query.trim());
  if (needle === '') return true;

  return (
    folded(source.naeCode).includes(needle) ||
    folded(source.sourceName).includes(needle) ||
    folded(source.emissionPointName).includes(needle) ||
    folded(source.siteLabel).includes(needle) ||
    source.substances.some((substance) => substanceMatches(substance, needle))
  );
}

export function filterSources(sources: readonly AirSource[], query: string): readonly AirSource[] {
  return query.trim() === '' ? sources : sources.filter((source) => sourceMatches(source, query));
}

/** Ключ рядка джерела: id рядка таблиці (унікальний), а не код НАЕ — один код повторюється під різними вузлами. */
export function sourceKey(source: AirSource): string {
  return String(source.id);
}

/**
 * AIR-ON-AIR: область рядка — код запису Region (Area) з імпорту (`regionEntryCode`/`areaEntryCode`), інакше текст REGION/AREA;
 * ділянка `-` (без ділянки) не показується. Порожньо — прочерк.
 */
export function scopeLabel(source: AirSource): string {
  const region = source.regionEntryCode ?? source.region;
  const areaText = source.areaEntryCode ?? source.area;
  const area = areaText === null || areaText === undefined || areaText === '' || areaText === '-' ? null : areaText;
  if (region === null || region === undefined || region === '') return cell(area);

  return area === null ? region : `${region} / ${area}`;
}

/*
 * P1 07.11 («користувач має БАЧИТИ ВСІ імпортовані дані»): колонки, додані до таблиць дозволу, і редагування рядка.
 */

/** Таблиця, до якої додано колонку: `sources` (AIR_SOURCES) чи `substances` (AIR_SOURCE_SUBSTANCES). */
export type AirTableKind = 'sources' | 'substances';

/** Додані колонки однієї таблиці в порядку таблиці. */
export function addedColumnsOf(data: AirLimitsPermit, table: AirTableKind): readonly AirAddedColumn[] {
  return (data.addedColumns ?? []).filter((column) => column.table === table);
}

/** Заголовок доданої колонки: назва мовою інтерфейсу, інакше англійська, інакше код. */
export function columnTitle(column: AirAddedColumn, lang: string): string {
  const names: Readonly<Record<string, string | undefined>> = column.nameL10n;
  const named = names[lang] ?? names['en'];

  return named === undefined || named.trim() === '' ? column.code : named;
}

/** Числова колонка (вирівнювання праворуч, `NumberInput`). */
export function isNumericColumn(column: AirAddedColumn): boolean {
  return column.dataType === 'Int' || column.dataType === 'Decimal';
}

type AddedValues = Readonly<Record<string, unknown>> | null | undefined;

/** Значення доданої колонки текстом форми: число й текст рядком, дата ISO, порожнє — `''`. */
export function addedRaw(values: AddedValues, code: string): string {
  const value = values?.[code];
  if (value === null || value === undefined) return '';
  if (typeof value === 'string') return value;
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);

  return JSON.stringify(value);
}

/** Значення доданої колонки для таблиці: число без хвостових нулів, порожнє — прочерк. */
export function addedText(values: AddedValues, column: AirAddedColumn): string {
  const raw = addedRaw(values, column.code);

  return isNumericColumn(column) ? num(raw) : cell(raw);
}

/** Вид поля форми редагування. */
export type EditKind = 'text' | 'number' | 'date';

/** Поле форми редагування рядка: код поля довідника (те, що бере пакетний запис), підпис, вид. */
export interface EditField {
  readonly code: string;
  readonly label: string;
  readonly kind: EditKind;
  /** Поле додане («Add as new column»): значення в `added`, а не у вбудованій властивості рядка. */
  readonly added: boolean;
}

type BuiltIn<T> = readonly [code: string, prop: keyof T, kind: EditKind, label: () => string];

/*
 * Вбудовані поля, які правляться на картці. ⛔ НАЕ, речовина, PERMIT, REGION і AREA тут не правляться: вони — ключ рядка й
 * область імпорту (зміна — повторний імпорт або сітка таблиці дозволу).
 */
const SourceBuiltIns: readonly BuiltIn<AirSource>[] = [
  ['SITE_LABEL', 'siteLabel', 'text', () => t('permits.air.edit.site')],
  ['PRODUCTION_CODE', 'productionCode', 'text', () => t('permits.air.edit.productionCode')],
  ['SOURCE_NAME', 'sourceName', 'text', () => t('permits.air.sources.source')],
  ['POINT_NAME', 'emissionPointName', 'text', () => t('permits.air.sources.point')],
  ['SOURCE_TYPE', 'sourceType', 'text', () => t('permits.air.sources.type')],
  ['QUANTITY', 'quantity', 'number', () => t('permits.air.sources.quantity')],
  ['HOURS_PER_YEAR', 'hoursPerYear', 'number', () => t('permits.air.sources.hours')],
  ['HEIGHT', 'height', 'number', () => t('permits.air.sources.height')],
  ['DIAMETER', 'diameter', 'number', () => t('permits.air.sources.diameter')],
  ['VELOCITY', 'velocity', 'number', () => t('permits.air.sources.velocity')],
  ['VOLUME', 'volume', 'number', () => t('permits.air.sources.volume')],
  ['TEMPERATURE', 'temperature', 'number', () => t('permits.air.sources.temperature')],
  ['X1', 'x1', 'number', () => t('permits.air.edit.x1')],
  ['Y1', 'y1', 'number', () => t('permits.air.edit.y1')],
  ['X2', 'x2', 'number', () => t('permits.air.edit.x2')],
  ['Y2', 'y2', 'number', () => t('permits.air.edit.y2')],
];

const SubstanceBuiltIns: readonly BuiltIn<AirSubstance>[] = [
  ['GRAMS_PER_SECOND', 'gramsPerSecond', 'number', () => t('permits.air.substances.gs')],
  ['MG_PER_NM3', 'mgPerNm3', 'number', () => t('permits.air.substances.mg')],
  ['TONS_PER_YEAR', 'tonsPerYear', 'number', () => t('permits.air.substances.ty')],
];

function editKind(column: AirAddedColumn): EditKind | null {
  switch (column.dataType) {
    case 'String':
      return 'text';
    case 'Int':
    case 'Decimal':
      return 'number';
    case 'Date':
      return 'date';
    default:
      // Bool / Lookup доданою колонкою картка лише показує: для них потрібен окремий вибір значення.
      return null;
  }
}

function fieldsOf<T>(builtIns: readonly BuiltIn<T>[], added: readonly AirAddedColumn[], lang: string): readonly EditField[] {
  const own = builtIns.map(([code, , kind, label]) => ({ code, label: label(), kind, added: false }));
  const extra = added.flatMap((column) => {
    const kind = editKind(column);

    return kind === null ? [] : [{ code: column.code, label: columnTitle(column, lang), kind, added: true }];
  });

  return [...own, ...extra];
}

/** Поля форми редагування джерела: вбудовані, далі додані колонки AIR_SOURCES. */
export function sourceEditFields(data: AirLimitsPermit, lang: string): readonly EditField[] {
  return fieldsOf(SourceBuiltIns, addedColumnsOf(data, 'sources'), lang);
}

/** Поля форми редагування речовини: g/s, mg/nm3, t/year, далі додані колонки AIR_SOURCE_SUBSTANCES. */
export function substanceEditFields(data: AirLimitsPermit, lang: string): readonly EditField[] {
  return fieldsOf(SubstanceBuiltIns, addedColumnsOf(data, 'substances'), lang);
}

function builtInRaw<T>(builtIns: readonly BuiltIn<T>[], row: T, code: string): string {
  const prop = builtIns.find(([own]) => own === code)?.[1];
  const value = prop === undefined ? null : row[prop];

  return value === null || value === undefined ? '' : String(value as string | number);
}

/** Початкові значення форми (текстом): десяткові — без хвостових нулів, щоб поле не показувало `0.5000000000000000`. */
export function initialValues(
  fields: readonly EditField[],
  row: AirSource | AirSubstance,
  kind: AirTableKind,
): Readonly<Record<string, string>> {
  const values: Record<string, string> = {};
  for (const field of fields) {
    const raw = field.added
      ? addedRaw(row.added, field.code)
      : kind === 'sources'
        ? builtInRaw(SourceBuiltIns, row as AirSource, field.code)
        : builtInRaw(SubstanceBuiltIns, row as AirSubstance, field.code);
    values[field.code] = field.kind === 'number' ? (raw === '' ? '' : num(raw)) : raw;
  }

  return values;
}

/**
 * Значення для пакетного запису — лише ЗМІНЕНІ поля (незмінене не пересилається: повна точність збереженого десяткового
 * лишається). Порожнє — `null` (очистити); число — JSON-числом (сервер читає текст за мовою користувача, а число — як є).
 */
export function changedValues(
  fields: readonly EditField[],
  initial: Readonly<Record<string, string>>,
  draft: Readonly<Record<string, string>>,
): Record<string, string | number | null> {
  const changed: Record<string, string | number | null> = {};
  for (const field of fields) {
    const before = initial[field.code] ?? '';
    const after = (draft[field.code] ?? '').trim();
    if (after === before.trim()) continue;
    if (after === '') changed[field.code] = null;
    else if (field.kind === 'number' && Number.isFinite(Number(after))) changed[field.code] = Number(after);
    else changed[field.code] = after;
  }

  return changed;
}
