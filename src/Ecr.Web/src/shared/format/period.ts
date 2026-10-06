import { hasText, t } from '@/shared/i18n';
import { formatDate } from './datetime';

/**
 * Календарний місяць мовою інтерфейсу: «September 2026», «Сентябрь 2026»,
 * «Қыркүйек 2026». `month` — 1…12; поза межами — порожній рядок (`A2-10`).
 *
 * ⛔ Назва місяця — з каталогу (`periods.month.1`…`.12`, `periods.monthOf`),
 * а не з `Intl`. `Intl.DateTimeFormat('kk', { month: 'long' })` у Chrome/Edge
 * дає «September 2026»: урізана ICU браузера казахської не має
 * (`supportedLocalesOf('kk')` → `[]`), і `formatLocale()` чесно відкочується
 * на en — підпис під полем «Кезең» виходив англійською. Node має повну ICU,
 * тому vitest цього не бачив. `ru` давав «сентябрь 2026 г.» — з рядкової
 * літери й із суфіксом, якого немає в жодному іншому підписі періоду.
 *
 * ⚠ Каталог ще не завантажено (перший рендер, тести без каталогу) — `Intl`,
 * як до `A2-10`, а не позначка `⟦…⟧`: підпис під полем — лише підказка.
 *
 * ⚠ Ключ місяця — шаблоном: сторож `EndpointCoverageTests` (`DynamicKeySites`)
 * знає всі дванадцять і звіряє їх із `09-seed.sql`. Шаблон, а не масив із
 * дванадцяти літералів, — заради бюджету чанків `PeriodsPage`/`DocumentsPage`.
 */
export function formatMonthYear(year: number, month: number): string {
  if (!Number.isInteger(year) || !Number.isInteger(month) || month < 1 || month > 12) return '';

  if (hasText('periods.monthOf') && hasText(`periods.month.${String(month)}`)) {
    return t('periods.monthOf', { month: formatMonthName(month), year: String(year) });
  }

  return formatDate(new Date(Date.UTC(year, month - 1, 1)), { year: 'numeric', month: 'long', timeZone: 'UTC' });
}

/**
 * Лише назва місяця мовою інтерфейсу («September», «Сентябрь», «Қыркүйек») —
 * для сітки місяців `PeriodPicker` (UI-13). Ті самі ключі й той самий відкат на
 * `Intl`, що й `formatMonthYear` вище; `month` поза 1…12 — порожній рядок.
 */
export function formatMonthName(month: number): string {
  if (!Number.isInteger(month) || month < 1 || month > 12) return '';

  const monthKey = `periods.month.${String(month)}`;

  return hasText(monthKey)
    ? t(monthKey)
    : formatDate(new Date(Date.UTC(2000, month - 1, 1)), { month: 'long', timeZone: 'UTC' });
}
