/**
 * Дата, НАБРАНА людиною в полі дати, → `Date` опівночі місцевого часу (A1-02).
 *
 * ⛔ Чому не розбір `@mantine/dates` за замовчуванням. Він кличе `dayjs(text, format)` БЕЗ плагіна
 * `customParseFormat`, тобто формат ігнорується і текст іде в `new Date(text)`:
 *   - `05.10.2026` рушій читає за американським порядком — 10 травня, а не 5 жовтня;
 *   - `2026-13-45` перекочується в 14 лютого 2027-го;
 *   - чого рушій не розібрав, поле на виході з нього мовчки стирає до попереднього значення.
 * Тут — лише точні форми, без перекочування; решта — `null`, і поле показує відмову.
 *
 * Форми за мовою інтерфейсу:
 *   - будь-яка мова — `yyyy-MM-dd`: формат, яким поле саме показує дату (`valueFormat`); те, що людина
 *     бачить у полі, мусить розбиратися назад;
 *   - ru/kz (і будь-яка мова, крім англійської) — ще й `dd.MM.yyyy` (день і місяць — одна чи дві
 *     цифри): так дату пишуть у цих мовах;
 *   - en — ЛИШЕ `yyyy-MM-dd`: `05.10.2026` англомовний читач прочитає і як 10 травня, тож у
 *     англійській ця форма неоднозначна, і вгадувати порядок ми не беремося.
 *
 * ⚠ Модуль без імпортів навмисно: тест поясів (`__tests__/userDate.zones.test.ts`) вантажить його
 * в окремому процесі Node з `TZ` у середовищі — так само, як `shared/format/dateOnly.ts`.
 */

const IsoDate = /^(\d{4})-(\d{2})-(\d{2})$/;
const DottedDate = /^(\d{1,2})\.(\d{1,2})\.(\d{4})$/;

/** Чи приймає мова форму `dd.MM.yyyy`: усі, крім англійської (`en`, `en-GB`…). */
export function acceptsDottedDate(language: string): boolean {
  return !/^en(?:$|-)/i.test(language);
}

/**
 * Рік/місяць/день → `Date` опівночі МІСЦЕВОГО часу, або `null`, якщо такого дня немає.
 *
 * ⚠ `setFullYear`, а не `new Date(y, m, d)`: конструктор читає роки 0–99 як 1900–1999.
 * ⛔ Неіснуючий день (`2026-02-30`, `2026-13-45`) — `null`, а не перекочений у наступний місяць.
 */
function localDay(year: number, month: number, day: number): Date | null {
  if (year < 1 || month < 1 || month > 12 || day < 1 || day > 31) return null;

  const result = new Date(2000, 0, 1, 0, 0, 0, 0);
  result.setFullYear(year, month - 1, day);

  return result.getFullYear() === year && result.getMonth() === month - 1 && result.getDate() === day
    ? result
    : null;
}

/**
 * Текст поля → дата; `null` — не дата в жодній допустимій для мови формі (порожній текст — теж `null`:
 * чи порожнє поле — помилка, вирішує поле, а не розбір).
 */
export function parseUserDate(text: string, language: string): Date | null {
  const value = text.trim();

  const iso = IsoDate.exec(value);
  if (iso !== null) return localDay(Number(iso[1]), Number(iso[2]), Number(iso[3]));

  if (!acceptsDottedDate(language)) return null;

  const dotted = DottedDate.exec(value);
  if (dotted !== null) return localDay(Number(dotted[3]), Number(dotted[2]), Number(dotted[1]));

  return null;
}

/**
 * `Date` → `yyyy-MM-dd` за місцевим календарем — те саме, що `formatDateOnly` (`shared/format`).
 *
 * ⚠ Копія свідома, а не недогляд: `DateOnlyInput` живе в лінивому чанку поля дати, і імпорт
 * `shared/format` додав би той чанк у карту передзавантаження маршрутів, що його не мали (бюджет
 * `D-132`, PeriodsPage). Розходження копій ловить `userDate.test.ts` («збігається з formatDateOnly»).
 */
export function formatIsoDay(date: Date): string {
  const pad = (part: number, width: number): string => String(part).padStart(width, '0');

  return `${pad(date.getFullYear(), 4)}-${pad(date.getMonth() + 1, 2)}-${pad(date.getDate(), 2)}`;
}
