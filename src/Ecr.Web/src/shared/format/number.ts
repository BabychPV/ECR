import type { Language } from '@/shared/i18n';
import { formatLocale } from './locale';

/**
 * Числа — локаллю продукту (`D15-09`).
 *
 * ⛔ Роздільники в мовах продукту РІЗНІ й перетинаються між собою: `en` пише
 * `1,234.5` (кома — тисячі), `ru`/`kk` — `1 234,5` (кома — дріб). Тобто той
 * самий рядок `1,234` читається як тисяча двісті тридцять чотири однією мовою
 * і як один і три десятих іншою. Для показників викидів це не питання смаку.
 */

/** Те саме, що в `datetime.ts`: порожньо на вході — порожньо на виході. */
const Nothing = '';

/**
 * Пам'ять форматувальників — з тієї ж причини, що в `datetime.ts`: сітка
 * документа малює тисячі числових комірок на кожному рендері.
 */
const formatters = new Map<string, Intl.NumberFormat>();

function formatter(locale: string, options: Intl.NumberFormatOptions): Intl.NumberFormat {
  const key = `${locale}|${JSON.stringify(options)}`;

  const hit = formatters.get(key);
  if (hit !== undefined) return hit;

  const made = new Intl.NumberFormat(locale, options);
  formatters.set(key, made);

  return made;
}

/**
 * Число мовою інтерфейсу.
 *
 * ⚠ `NaN` дає порожньо, а `Infinity` — НІ (`Intl` пише `∞`). Різниця свідома:
 * `NaN` означає «значення немає» і показувати три латинські літери посеред
 * казахської таблиці нема за що; нескінченність — це РЕЗУЛЬТАТ обчислення
 * (ділення на нуль у формулі), і ховати його означало б показати порожню
 * комірку там, де формула зламана.
 */
export function formatNumber(
  value: number | null | undefined,
  options?: Intl.NumberFormatOptions,
  lang?: Language,
): string {
  if (value === null || value === undefined || Number.isNaN(value)) return Nothing;

  return formatter(formatLocale(lang), options ?? {}).format(value);
}
