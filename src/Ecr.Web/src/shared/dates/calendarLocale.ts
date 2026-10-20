import 'dayjs/locale/ru';
import 'dayjs/locale/kk';

/**
 * Мова календаря `@mantine/dates` за мовою інтерфейсу (P3-N5, прохід B): без `locale` назва місяця й дні тижня лишалися
 * англійськими («January 2026») у ru/kz, хоча кнопки «попередній/наступний місяць» уже були локалізовані.
 *
 * ⚠ Локалі `dayjs` вантажаться ТУТ (побічними імпортами), а `StrictDateInput` живе в лінивому чанку поля дати
 * (`DateInputWithStyles`) - хто календар не відкривав, не платить за ~2 КБ локалей. Казахська в застосунку - `kz`, у dayjs - `kk`.
 */
export function calendarLocale(lang: string): 'en' | 'ru' | 'kk' {
  if (lang === 'ru') return 'ru';
  if (lang === 'kz' || lang === 'kk') return 'kk';

  return 'en';
}
