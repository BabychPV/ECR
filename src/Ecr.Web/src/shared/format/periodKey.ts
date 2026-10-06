import { t } from '@/shared/i18n';
import { formatMonthYear } from './period';

const PeriodKeyMultiplier = 100;

/**
 * Людська назва періоду за `periodKey` (`YYYYMM` / `Year*100 + Sequence`, `R-A6`)
 * мовою інтерфейсу: «October 2026», «Қазан 2026», «Q4 2025», «2026».
 * Порожній рядок — ключ не є періодом заданої періодичності.
 *
 * ⚠ Один форматер на ВСІ місця, де людині показують період (поле вибору,
 * список у формі створення документа): ключ `202610` лишається значенням для
 * API й адреси, а не текстом на екрані. `kind` — `PeriodKind` проєкту; без
 * нього — місяць (`X-34`). Місяць — через `formatMonthYear` (`A2-10`).
 *
 * ⚠ Окремий модуль, а не `period.ts`: той імпортує й `PeriodsPage`, якій цей
 * форматер не потрібен, — бюджет її чанка не росте.
 */
export function formatPeriodKey(periodKey: number, kind?: string): string {
  const year = Math.trunc(periodKey / PeriodKeyMultiplier);
  const sequence = periodKey - year * PeriodKeyMultiplier;
  // NaN і нецілі ключі: порівняння нижче хибні, а `formatMonthYear` відсікає їх сам.
  const valid = sequence >= 1 && Number.isInteger(sequence);

  if (kind === 'Quarterly') return valid && sequence <= 4 ? t('periods.quarterOf', { quarter: sequence, year }) : '';
  if (kind === 'Yearly') return sequence === 1 ? String(year) : '';
  if (kind === 'Custom') return valid ? t('periods.customOf', { sequence, year }) : '';

  return formatMonthYear(year, sequence);
}
