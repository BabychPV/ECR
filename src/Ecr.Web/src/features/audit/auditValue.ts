import { formatDate, formatDecimal } from '@/shared/format';

/** Типи колонок, чиє значення — число (`U-05`: одне правило подачі числа). */
export const NumericTypes: readonly string[] = ['Decimal', 'Int', 'Formula', 'Calculated'];

/**
 * Значення журналу — за правилом показу, а не у форматі сховища (`X-35`).
 *
 * ⛔ Журнал показував «Was 53.1771000000000000»: `aud.CellChange` зберігає
 * число з повним масштабом колонки сховища (`decimal(34,16)`, `D-148`).
 * Людина набирала `53.1771` і шукає в журналі саме його. Хвостові нулі
 * прибирає той самий `formatDecimal`, що й сітка (`U-05`/`U-24`), розряди —
 * мовою інтерфейсу.
 *
 * ⚠ Нерозібране значення показується ЯК Є: журнал — доказ, і сховати дивне
 * значення за «—» означало б сховати саму розбіжність.
 */
export function auditValueText(value: string | null | undefined, dataType: string | null | undefined): string {
  if (value === null || value === undefined) return '—';
  if (dataType === null || dataType === undefined) return value;

  if (NumericTypes.includes(dataType)) return formatDecimal(value) ?? value;

  if (dataType === 'Date') {
    const day = /^\d{4}-\d{2}-\d{2}/.exec(value)?.[0];
    const shown = day === undefined ? '' : formatDate(day);

    return shown.length > 0 ? shown : value;
  }

  return value;
}
