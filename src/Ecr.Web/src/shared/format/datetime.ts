import type { Language } from '@/shared/i18n';
import { formatLocale } from './locale';

/**
 * Дати й час — локаллю продукту (`D15-09`).
 *
 * ⛔ Порядок складників дати НЕ косметичний: `03.09.2026` і `9/3/2026` — це
 * третє вересня і дев'яте березня. У звітності про викиди різниця між ними
 * вартує стільки ж, скільки помилка в цифрі.
 */

/**
 * Що приймається на вхід.
 *
 * ⚠ `string` тут не для зручності: сервер віддає моменти рядком ISO-8601, і
 * вимога робити `new Date(...)` на кожному місці виклику означала б, що
 * перевірка на «дата не розібралася» теж живе на кожному місці виклику — тобто
 * подекуди її не буде.
 */
export type DateLike = Date | string | number | null | undefined;

/**
 * Пам'ять форматувальників.
 *
 * ⛔ `new Intl.DateTimeFormat(...)` — найдорожчий виклик у цьому модулі
 * (розбір тега, підняття даних ICU). Сітка документа малює тисячі комірок, і
 * без кешу кожен рендер створював би тисячі однакових об'єктів.
 */
const formatters = new Map<string, Intl.DateTimeFormat>();

function formatter(locale: string, options: Intl.DateTimeFormatOptions): Intl.DateTimeFormat {
  const key = `${locale}|${JSON.stringify(options)}`;

  const hit = formatters.get(key);
  if (hit !== undefined) return hit;

  /*
   * ⚠ Тут навмисно НЕМАЄ `try`. Локаль уже перевірена в `formatLocale()` —
   * вона приходить ззовні (рядок із реєстру мов) і кинути не може. А `options`
   * приходять із НАШОГО ж коду, літералом на місці виклику: проковтнути
   * помилку в них означало б мовчки показати не те, що написано в коді.
   * Заборона з умови стосується саме зовнішнього рядка конфігурації, а не
   * власних помилок програмування.
   */
  const made = new Intl.DateTimeFormat(locale, options);
  formatters.set(key, made);

  return made;
}

/**
 * Момент або `null`, якщо його встановити не вдалося.
 *
 * ⚠ `Number.isNaN(getTime())` — єдина працююча перевірка: `new Date('дурниця')`
 * повертає ОБ'ЄКТ `Date`, а не `null` і не виняток.
 */
function moment(value: DateLike): Date | null {
  if (value === null || value === undefined) return null;

  const at = value instanceof Date ? value : new Date(value);

  return Number.isNaN(at.getTime()) ? null : at;
}

/**
 * Порожньо на вході — порожньо на виході.
 *
 * ⚠ НЕ «сьогодні» і не прочерк. Підставити поточний час замість відсутнього
 * означало б збрехати про дані (той самий клас, що `BE-06`), а вибір видимої
 * позначки («—», «немає даних») належить екрану: він знає, чи це порожня
 * комірка таблиці, чи пропущене поле картки. Модуль форматування каталогу
 * рядків не бачить і вигадувати напис не має права.
 */
const Nothing = '';

const DateOptions: Intl.DateTimeFormatOptions = { dateStyle: 'medium' };
const DateTimeOptions: Intl.DateTimeFormatOptions = { dateStyle: 'medium', timeStyle: 'short' };
const TimeOptions: Intl.DateTimeFormatOptions = { timeStyle: 'short' };

/** Дата без часу, мовою інтерфейсу. */
export function formatDate(value: DateLike, options?: Intl.DateTimeFormatOptions, lang?: Language): string {
  return format(value, options ?? DateOptions, lang);
}

/** Дата з годиною й хвилиною, мовою інтерфейсу. */
export function formatDateTime(value: DateLike, options?: Intl.DateTimeFormatOptions, lang?: Language): string {
  return format(value, options ?? DateTimeOptions, lang);
}

/**
 * Лише час.
 *
 * ⚠ 12- чи 24-годинний запис визначає ЛОКАЛЬ, а не наш вибір: `en` дає
 * `2:05 PM`, `ru` і `kk` — `14:05`. Саме тому тут `timeStyle`, а не
 * `hour`/`minute` з `hour12`: другий варіант нав'язав би одній з мов чужий
 * для неї запис.
 */
export function formatTime(value: DateLike, options?: Intl.DateTimeFormatOptions, lang?: Language): string {
  return format(value, options ?? TimeOptions, lang);
}

function format(value: DateLike, options: Intl.DateTimeFormatOptions, lang?: Language): string {
  const at = moment(value);
  if (at === null) return Nothing;

  return formatter(formatLocale(lang), options).format(at);
}
