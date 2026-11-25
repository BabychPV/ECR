import { formatMonthName, formatPeriodKey } from '@/shared/format';

/** Діючий виняток «місяць відкрито для документа» (`reopenedPeriods` картки). */
export interface ReopenedMonth {
  readonly periodKey: number;
  readonly openUntil: string;
}

/** Діапазон суміжних місяців з ОДНАКОВИМ строком. */
export interface ReopenedRange {
  readonly from: number;
  readonly to: number;
  /** Строк словами в поясі майданчика (той самий текст для всіх місяців діапазону). */
  readonly until: string;
  /** «Jan–Sep 2026», «Sep 2026», «Dec 2025–Feb 2026». */
  readonly months: string;
}

/** Наступний місяць ключа `yyyyMM` (грудень → січень наступного року). */
function nextMonth(periodKey: number): number {
  return periodKey % 100 === 12 ? (Math.trunc(periodKey / 100) + 1) * 100 + 1 : periodKey + 1;
}

/** Коротка назва місяця («Jan»): перші три літери повної назви мовою інтерфейсу. */
function shortMonth(periodKey: number): string {
  return formatMonthName(periodKey % 100).slice(0, 3);
}

/** Підпис діапазону: один місяць — повна назва; у межах року — «Jan–Sep 2026»; через рік — «Dec 2025–Feb 2026». */
export function rangeLabel(from: number, to: number): string {
  if (from === to) return formatPeriodKey(from);
  const fromYear = Math.trunc(from / 100);
  const toYear = Math.trunc(to / 100);

  return fromYear === toYear
    ? `${shortMonth(from)}–${shortMonth(to)} ${String(toYear)}`
    : `${shortMonth(from)} ${String(fromYear)}–${shortMonth(to)} ${String(toYear)}`;
}

/**
 * ✎ b14 doc-notices-compact (людина 25.11: «ці повідомлення багато забирають місця»): перевідкриті місяці — один чип у
 * шапці замість списку з 9 рядків. Суміжні місяці з ОДНАКОВИМ строком згортаються в діапазон; різні строки (або
 * розрив) — окремі діапазони, за зростанням місяця.
 *
 * @param untilText строк словами (пояс майданчика); місяці з однаковим ТЕКСТОМ строку — один діапазон: людина бачить
 *   саме текст, і два моменти в межах однієї хвилини для неї один строк.
 */
export function reopenedRanges(
  periods: readonly ReopenedMonth[],
  untilText: (openUntil: string) => string,
): ReopenedRange[] {
  const sorted = [...periods].sort((a, b) => a.periodKey - b.periodKey);
  const ranges: { from: number; to: number; until: string }[] = [];

  for (const period of sorted) {
    const until = untilText(period.openUntil);
    const last = ranges.at(-1);
    if (last !== undefined && last.until === until && nextMonth(last.to) === period.periodKey) {
      last.to = period.periodKey;
    } else {
      ranges.push({ from: period.periodKey, to: period.periodKey, until });
    }
  }

  return ranges.map((range) => ({ ...range, months: rangeLabel(range.from, range.to) }));
}
