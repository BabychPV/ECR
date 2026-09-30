import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cellKey } from './permissions';
import { decimalTextOf } from './rounding';

/**
 * Рядок підсумків сітки документа (`UI-08`).
 *
 * ⛔ Підсумок рахується НА КЛІЄНТІ, і це названо рішенням, а не замовчано.
 * Контракт зрізу (`TableSliceDto`) агрегатів не несе взагалі: ні поля суми в
 * `ColumnDto`, ні окремого рядка підсумків у `rows` — перевірено по
 * `Ecr.Application/Documents/Dto/TableSliceDto.cs`. Заводити заради цього
 * ендпоінт означало б попросити сервер повторити додавання тих самих чисел,
 * які клієнт уже тримає в пам'яті, — і показати при цьому СТАРУ суму, бо
 * незбережена правка серверу ще не відома.
 *
 * ⛔ Тому правило одне й просте: підсумок — це сума рівно тих значень, які
 * ВИДНО в сітці. Саме тому `valueAt` нижче дослівно повторює порядок
 * `DocumentGrid.gridRows` (правка → підтверджене значення → комірка →
 * `defaultValue` колонки, `ФВ-3.8`): розійшовшись із ним, підсумок показував
 * би суму чисел, яких на екрані немає.
 *
 * ⛔ Арифметика — РЯДКОВА, через `BigInt`. `decimal(25,16)` не вкладається в
 * `double`: `Number('1234.1234567890123456')` уже втрачає три знаки
 * (`shared/format/decimal.ts`), а сума п'ятисот таких значень накопичила б
 * похибку в самому підсумковому рядку — тобто рівно там, куди дивиться
 * інспектор. Канон входу дає `decimalTextOf` (`rounding.ts`), а не другий
 * розбір числа поруч із ним.
 */

/** Підсумок однієї колонки. */
export interface ColumnTotal {
  /** Сума заповнених комірок — десятковий рядок у канонічному записі. */
  readonly sum: string;
  /** Скільки комірок у ній узяло участь. */
  readonly count: number;
}

/**
 * Типи колонок, які підсумовуються.
 *
 * ⚠ Це ті самі чотири типи, що й `isNumericColumn` (`cellValue.ts`): що
 * підсумовується, те й малюється числом. До `U-05` цей перелік був
 * ШИРШИЙ рівно на `Int` — і саме ця розбіжність давала рядок підсумків,
 * у якому сума цілої колонки подана інакше, ніж сума сусідньої.
 *
 * ⛔ Вужче за «усе, що є числом» рівно на `Lookup`: його комірка тримає
 * `ValueRegistryEntryId` (`edits.coerce`) — число, сума якого не означає
 * нічого, а виглядає як вимірювання.
 */
const TotalableColumnTypes: ReadonlySet<string> = new Set([
  'Decimal',
  'Int',
  'Formula',
  'Calculated',
]);

/** Чи підсумовується ця колонка. */
export function isTotalableColumn(column: ColumnDto): boolean {
  return TotalableColumnTypes.has(column.dataType);
}

/** Мінімальна форма незбереженої правки, потрібна підсумку. */
export interface TotalsEdit {
  readonly rowKey: string;
  readonly columnCode: string;
  readonly value: unknown;
}

/** Джерела значень поверх зрізу — ті самі, що бачить `gridRows`. */
export interface TotalsOverlay {
  /** Незбережені правки цього зрізу (`pendingStore`), ключ — `rowKey:columnCode`. */
  readonly pending?: ReadonlyMap<string, TotalsEdit>;
  /** Значення, підтверджені оператором (`ФВ-2.16`), ключ — `rowKey:columnCode`. */
  readonly overrides?: ReadonlyMap<string, unknown>;
}

/**
 * Значення комірки, ЯК ЙОГО ВИДНО в сітці.
 *
 * ⚠ Порядок дослівно той самий, що в `DocumentGrid.gridRows` плюс шар
 * незбережених правок, яких та функція не знає (вони живуть у моделі RevoGrid,
 * а не в масиві `source`). Без цього шару підсумок відставав би від екрана на
 * все вікно автозбереження — 500 мс тиші плюс час запиту.
 */
function valueAt(
  slice: TableSliceDto,
  rowIndex: number,
  column: ColumnDto,
  overlay: TotalsOverlay,
): unknown {
  const row = slice.rows[rowIndex];
  if (row === undefined) return null;

  const key = cellKey(row.rowKey, column.code);

  const edit = overlay.pending?.get(key);
  if (edit !== undefined) return edit.value;

  if (overlay.overrides?.has(key) === true) return overlay.overrides.get(key);

  return row.cells[column.code] ?? column.defaultValue ?? '';
}

/**
 * Число комірки для підсумку; `null` — комірка в суму НЕ йде.
 *
 * ⛔ Порожня комірка — це `null`, а не нуль. «Не заповнювали» і «нуль»
 * різняться у звіті регулятора (той самий рядок стоїть над `cellDisplay`), і
 * підсумок, який рахує порожнечу нулем, ще й псує `count`: колонка з двома
 * заповненими комірками з п'ятисот звітувала б «500 значень».
 *
 * ⛔ Нечислове значення (`'н/д'`, яке `coerce` лишає текстом до відповіді
 * `ECR-CELL-0422`) теж НЕ стає нулем — воно просто не бере участі. Мовчазний
 * нуль тут читався б як виміряне значення.
 */
function addendOf(value: unknown): string | null {
  if (value === null || value === undefined) return null;
  if (typeof value === 'boolean') return null;

  const text = typeof value === 'string' ? value : String(value);
  if (text.trim().length === 0) return null;

  return decimalTextOf(text);
}

/** Десятковий рядок → ціле в масштабі `scale` (кількість знаків після коми). */
function scaledOf(decimal: string, scale: number): bigint {
  const negative = decimal.startsWith('-');
  const magnitude = negative ? decimal.slice(1) : decimal;
  const dot = magnitude.indexOf('.');

  const int = dot < 0 ? magnitude : magnitude.slice(0, dot);
  const frac = dot < 0 ? '' : magnitude.slice(dot + 1);

  const digits = int + frac.padEnd(scale, '0');
  const value = BigInt(digits);

  return negative ? -value : value;
}

/** Ціле в масштабі `scale` → канонічний десятковий рядок. */
function unscale(value: bigint, scale: number): string {
  const negative = value < 0n;
  const digits = (negative ? -value : value).toString().padStart(scale + 1, '0');

  const int = digits.slice(0, digits.length - scale);
  const frac = scale === 0 ? '' : digits.slice(digits.length - scale).replace(/0+$/, '');

  const magnitude = frac.length === 0 ? int : `${int}.${frac}`;

  // ⚠ `-0` зводиться до `0` — тим самим правилом, що й `normalizeDecimal`:
  // `decimal` від'ємного нуля не має.
  return magnitude === '0' ? '0' : (negative ? '-' : '') + magnitude;
}

/**
 * Підсумки колонок, які підсумовуються; колонка без жодного заповненого
 * значення у мапу НЕ потрапляє.
 *
 * ⛔ Саме «не потрапляє», а не «нуль»: порожня колонка з написом `0` у рядку
 * підсумків — це те саме, проти чого застерігає `addendOf`, лише на рівні
 * колонки. Читач бачить виміряний нуль там, де не виміряно нічого.
 */
export function columnTotals(
  slice: TableSliceDto,
  overlay: TotalsOverlay = {},
): Map<string, ColumnTotal> {
  const totals = new Map<string, ColumnTotal>();

  for (const column of slice.columns) {
    if (!isTotalableColumn(column)) continue;

    const addends: string[] = [];

    for (let rowIndex = 0; rowIndex < slice.rows.length; rowIndex++) {
      const addend = addendOf(valueAt(slice, rowIndex, column, overlay));
      if (addend !== null) addends.push(addend);
    }

    if (addends.length === 0) continue;

    // ⚠ Масштаб суми — найдовший дріб серед доданків, а не `column.scale`:
    // колонка може не оголошувати масштабу зовсім, і тоді зведення до нуля
    // знаків мовчки відкинуло б дробові частини ще до додавання.
    const scale = addends.reduce((longest, addend) => {
      const dot = addend.indexOf('.');

      return dot < 0 ? longest : Math.max(longest, addend.length - dot - 1);
    }, 0);

    let sum = 0n;
    for (const addend of addends) sum += scaledOf(addend, scale);

    totals.set(column.code, { sum: unscale(sum, scale), count: addends.length });
  }

  return totals;
}

/**
 * Службовий ключ рядка підсумків.
 *
 * ⛔ Подвійне підкреслення — та сама домовленість, що в `__rowKey`/`__rowLabel`
 * (`DocumentGrid.tsx`): значення не має права збігтися з `RowKey` справжнього
 * рядка. Ключі рядків видає сервер як GUID у форматі `N` (`ФВ-3.2`), тож
 * підкреслень у них не буває.
 */
export const TotalsRowKey = '__totals';

/** Рядок підсумків у моделі сітки. */
export type GridTotalsRow = Record<string, unknown> & { __rowKey: string };

/** Чи це модель рядка підсумків, а не рядка документа. */
export function isTotalsRow(model: unknown): boolean {
  return (
    typeof model === 'object' &&
    model !== null &&
    (model as { __rowKey?: unknown }).__rowKey === TotalsRowKey
  );
}

/**
 * Модель закріпленого рядка підсумків (`pinnedBottomSource` RevoGrid).
 *
 * @param caption Підпис рядка з каталогу; літерала тут бути не може.
 * @param labelProp Ім'я властивості колонки підпису рядків, якщо ця колонка в
 * сітці є; `null` — її немає.
 *
 * ⚠ Куди лягає підпис. Є колонка підпису — у неї: вона перша й лише для
 * читання, тобто рівно те місце, де око шукає назву рядка. Немає — підпис іде
 * в першу НЕпідсумовувану колонку (текст, дата, довідник): у неї підсумку
 * однаково немає, а рядок без підпису читається як іще один рядок даних.
 * Немає й такої — рядок лишається самими числами; вигадувати для підпису
 * зайву колонку означало б посунути всі дані вбік заради напису.
 */
export function totalsRow(
  slice: TableSliceDto,
  totals: ReadonlyMap<string, ColumnTotal>,
  caption: string,
  labelProp: string | null,
): GridTotalsRow {
  const row: GridTotalsRow = { __rowKey: TotalsRowKey };

  for (const column of slice.columns) {
    row[column.code] = totals.get(column.code)?.sum ?? '';
  }

  if (labelProp !== null) {
    row[labelProp] = caption;

    return row;
  }

  const firstPlain = slice.columns.find((column) => !isTotalableColumn(column));
  if (firstPlain !== undefined) row[firstPlain.code] = caption;

  return row;
}
