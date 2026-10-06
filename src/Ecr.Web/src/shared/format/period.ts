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

  const monthKey = `periods.month.${String(month)}`;

  if (hasText('periods.monthOf') && hasText(monthKey)) {
    return t('periods.monthOf', { month: t(monthKey), year: String(year) });
  }

  return formatDate(new Date(Date.UTC(year, month - 1, 1)), { year: 'numeric', month: 'long', timeZone: 'UTC' });
}
