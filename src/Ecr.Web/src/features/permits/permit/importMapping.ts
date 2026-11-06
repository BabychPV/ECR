import type { PermitField } from './model';

/*
 * Зіставлення стовпців файлу зі стовпцями таблиці дозволу («Paste from Excel…» / «Import file…», людина 06.11: макети файлів
 * різні). Без React і запитів.
 *
 * - Із заголовком: спершу збережене зіставлення таблиці (сервер, спільне для всіх: нормалізований заголовок → код стовпця), потім
 *   автозіставлення за заголовком (назва стовпця будь-якою мовою каталогу АБО код). Кожен стовпець таблиці - щонайбільше одному
 *   стовпцю файлу (перший виграє); решта - «ignore».
 * - Без заголовка: за порядком (стовпець файлу N → стовпець таблиці N), зайві праворуч - «ignore». Обидва варіанти людина
 *   змінює в діалозі.
 * - Незіставлений стовпець таблиці лишається порожнім.
 */

/** Зіставлення: на кожен стовпець файлу - код стовпця таблиці або `null` («ignore»). */
export type ColumnMapping = readonly (string | null)[];

/**
 * Заголовок як ключ зіставлення: NFKC, нижній регістр, лише літери й цифри. Сервер нормалізує так само
 * (`PermitImportTables.NormalizeHeader`), тож збережене зіставлення знаходиться за будь-якого написання.
 */
export function normalizeHeader(text: string): string {
  return text.normalize('NFKC').toLowerCase().replace(/[^\p{L}\p{N}]/gu, '');
}

/** Ключі, за якими стовпець таблиці впізнається в заголовку: назва кожною мовою, підпис екрана й код. */
function fieldKeys(field: PermitField): Set<string> {
  const keys = new Set<string>();
  for (const name of [...Object.values(field.source.nameL10n), field.column.label, field.column.code]) {
    const key = normalizeHeader(name);
    if (key !== '') keys.add(key);
  }
  return keys;
}

/** Стовпець таблиці для заголовка: збережене зіставлення, потім назва/код; `null` - не впізнано. */
function matchHeader(
  header: string,
  fields: readonly PermitField[],
  saved: Readonly<Record<string, string>>,
  keys: readonly Set<string>[],
): string | null {
  const key = normalizeHeader(header);
  if (key === '') return null;
  const stored = saved[key];
  if (stored !== undefined && fields.some((field) => field.column.code === stored)) return stored;
  const index = keys.findIndex((set) => set.has(key));
  return index < 0 ? null : (fields[index]?.column.code ?? null);
}

/** Автозіставлення за заголовком (збережене → назва/код); стовпець таблиці береться один раз - першим стовпцем файлу. */
export function autoMapping(
  headers: readonly string[],
  fields: readonly PermitField[],
  saved: Readonly<Record<string, string>> = {},
): ColumnMapping {
  const keys = fields.map(fieldKeys);
  const taken = new Set<string>();
  return headers.map((header) => {
    const code = matchHeader(header, fields, saved, keys);
    if (code === null || taken.has(code)) return null;
    taken.add(code);
    return code;
  });
}

/** Без заголовка: стовпець файлу N → стовпець таблиці N; зайві праворуч - «ignore». */
export function orderMapping(width: number, fields: readonly PermitField[]): ColumnMapping {
  return Array.from({ length: width }, (_, index) => fields[index]?.column.code ?? null);
}

/**
 * Чи схожий перший рядок на заголовок: щонайменше половина непорожніх клітинок (і хоч одна) впізнається як назва чи код
 * стовпця таблиці або як збережений заголовок. Дані («Outfall C», «5,5») не впізнаються.
 */
export function looksLikeHeader(
  first: readonly string[],
  fields: readonly PermitField[],
  saved: Readonly<Record<string, string>> = {},
): boolean {
  const cells = first.filter((cell) => cell.trim() !== '');
  if (cells.length === 0) return false;
  const keys = fields.map(fieldKeys);
  const hits = cells.filter((cell) => matchHeader(cell, fields, saved, keys) !== null).length;
  return hits > 0 && hits * 2 >= cells.length;
}

/** Що заважає застосувати зіставлення: обов'язкові незіставлені стовпці й стовпці, зіставлені двічі. */
export interface MappingProblems {
  /** Обов'язкові стовпці таблиці, яким не дісталось жодного стовпця файлу (окрім тих, що виводяться з НАЕ). */
  readonly missingRequired: readonly string[];
  /** Стовпці таблиці, зіставлені з кількома стовпцями файлу. */
  readonly duplicated: readonly string[];
}

/**
 * Перевірка зіставлення. `derivable` - стовпці Region/Area, які сервер виводить із джерела НАЕ: вони не «відсутні», якщо
 * зіставлено хоч один стовпець НАЕ (`naeColumns`).
 */
export function mappingProblems(
  mapping: ColumnMapping,
  fields: readonly PermitField[],
  derivable: readonly string[] = [],
  naeColumns: readonly string[] = [],
): MappingProblems {
  const used = mapping.filter((code): code is string => code !== null);
  const naeMapped = naeColumns.some((code) => used.includes(code));
  const missingRequired = fields
    .filter((field) => field.column.required === true && !used.includes(field.column.code))
    .filter((field) => !(naeMapped && derivable.includes(field.column.code)))
    .map((field) => field.column.code);
  const duplicated = [...new Set(used.filter((code, index) => used.indexOf(code) !== index))];
  return { missingRequired, duplicated };
}

/** Рядки файлу → рядки в порядку стовпців таблиці (незіставлений стовпець - порожня клітинка). */
export function mappedLines(
  lines: readonly (readonly string[])[],
  mapping: ColumnMapping,
  fields: readonly PermitField[],
): string[][] {
  const source = fields.map((field) => mapping.indexOf(field.column.code));
  return lines.map((line) => source.map((index) => (index < 0 ? '' : (line[index] ?? ''))));
}

/**
 * Зіставлення для збереження: нормалізований заголовок → код стовпця (лише зіставлені й непорожні заголовки). Сервер нормалізує
 * ключі ще раз.
 */
export function mappingToSave(headers: readonly string[], mapping: ColumnMapping): Record<string, string> {
  const result: Record<string, string> = {};
  headers.forEach((header, index) => {
    const code = mapping[index];
    const key = normalizeHeader(header);
    if (code != null && key !== '') result[key] = code;
  });
  return result;
}
