import type { TableSliceDto } from '@/api/types';
import { cellKey } from './permissions';
import { addendOf, scaledOf, unscale, type TotalsEdit } from './gridTotals';
import type { GridSelection } from './selection';

/** Підсумок виділених числових комірок (UI-23). */
export interface SelectionStats {
  /** Скільки числових непорожніх комірок у виділенні. */
  readonly count: number;
  /** Сума — канонічний десятковий рядок. */
  readonly sum: string;
  /** Середнє — канонічний десятковий рядок, округлене до `AverageExtraDigits` знаків понад масштаб. */
  readonly average: string;
}

/** Скільки знаків після коми середнє має понад найбільший масштаб доданків. */
const AverageExtraDigits = 4;

interface SelectionStatsInput {
  readonly slice: TableSliceDto;
  /** Колонки, які отримала сітка (з колонкою підпису, якщо вона є). */
  readonly columns: readonly { readonly prop?: string | number }[];
  /** Моделі рядків, які отримала сітка. */
  readonly rows: readonly Record<string, unknown>[];
  readonly range: GridSelection['range'] | null;
  /** Незбережені правки, ключ — `rowKey:columnCode`. */
  readonly pending?: ReadonlyMap<string, TotalsEdit>;
}

/**
 * Count / Sum / Average виділеного діапазону, як у рядку стану Excel (макет
 * `screen-document.js`, `selstats`).
 *
 * ⛔ Арифметика — рядкова через `BigInt` (`gridTotals.ts`): `decimal(25,16)`
 * не вкладається в `Number`. Порожні й нечислові комірки не беруть участі
 * (не стають нулем), як і в рядку підсумків. Колонка підпису рядка й колонки
 * без коду зрізу пропускаються.
 *
 * ⚠ `null` — підсумку немає: виділення однієї комірки (макет тоді показує
 * розмір таблиці) або в діапазоні менше двох чисел.
 */
export function selectionStats(input: SelectionStatsInput): SelectionStats | null {
  const { slice, columns, rows, range, pending } = input;
  if (range === null) return null;
  if (range.fromRow === range.toRow && range.fromColumn === range.toColumn) return null;

  const codes = new Set(slice.columns.map((column) => column.code));
  const addends: string[] = [];

  for (let columnIndex = range.fromColumn; columnIndex <= range.toColumn; columnIndex += 1) {
    const prop = columns[columnIndex]?.prop;
    if (prop === undefined || !codes.has(String(prop))) continue;

    const code = String(prop);

    for (let rowIndex = range.fromRow; rowIndex <= range.toRow; rowIndex += 1) {
      const row = slice.rows[rowIndex];
      const model = rows[rowIndex];
      if (row === undefined || model === undefined) continue;

      const edit = pending?.get(cellKey(row.rowKey, code));
      const addend = addendOf(edit === undefined ? model[code] : edit.value);
      if (addend !== null) addends.push(addend);
    }
  }

  if (addends.length < 2) return null;

  const scale = Math.max(...addends.map(scaleOf));
  const total = addends.reduce((sum, addend) => sum + scaledOf(addend, scale), 0n);

  const averageScale = scale + AverageExtraDigits;
  const widened = total * 10n ** BigInt(AverageExtraDigits);
  const count = BigInt(addends.length);

  // Округлення половини від нуля, як `decimal` сервера (`MidpointRounding.AwayFromZero`).
  const quotient = widened / count;
  const remainder = widened % count;
  const twice = (remainder < 0n ? -remainder : remainder) * 2n;
  const average = twice >= count ? quotient + (widened < 0n ? -1n : 1n) : quotient;

  return { count: addends.length, sum: unscale(total, scale), average: unscale(average, averageScale) };
}

function scaleOf(decimal: string): number {
  const dot = decimal.indexOf('.');

  return dot === -1 ? 0 : decimal.length - dot - 1;
}
