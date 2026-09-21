import type { Language } from '@/shared/i18n';
// ⚠ Канон десяткового рядка — НЕ тут. Він уже є в `./decimal`
// (`normalizeDecimal`, коміт `d9e75a82`), і другий нормалізатор поруч із
// першим розійшовся б із ним — рівно той клас дефектів, який цей перехід і
// виправляє. Тут лишається тільки ПОДАННЯ: локаль, роздільники, знаки.
import { normalizeDecimal } from './decimal';
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

/* ────────────────────────────────────────────────────────────────────────────
 * ПОДАННЯ десяткового, який приїхав РЯДКОМ (`e470777a`, `02-contracts.md` §8).
 * ──────────────────────────────────────────────────────────────────────────── */

/**
 * ⛔ Чому не `formatNumber(Number(value))`.
 *
 * Сервер віддає `decimal` рядком саме тому, що `JSON.parse` веде число через
 * IEEE-754 і 16-й знак `decimal(28,16)` зникає мовчки. `Number()` перед
 * форматуванням повернув би ту саму втрату на крок пізніше: замір у цьому
 * репозиторії — `Number('1234.1234567890123456')` дає `1234.1234567890124`,
 * тобто три знаки з двадцяти. Тому шлях до екрана лишається рядковим.
 *
 * ⚠ Правило локалі при цьому ОДНЕ на обидві половини файлу: `formatLocale()` і
 * той самий `Intl.NumberFormat`, тобто `en` → `1,234.5`, `ru`/`kk` →
 * `1 234,5`. Другого правила тут не заводиться.
 */

/**
 * Стеля `maximumFractionDigits`, якої вистачає і яку точно приймають.
 *
 * ⚠ ES2023 підняв межу `Intl` до 100, але доти специфікація давала 20, і
 * рантайм старішої версії кидає `RangeError` на 21. Масштаб десяткових тут —
 * 16 (`NumericPolicy.OutputScale`, `decimal(28,16)`), тож 20 — запас, а не
 * обмеження; довший дріб був би показаний округленим, і це назване, а не
 * сховане.
 */
const MaxIntlFractionDigits = 20;

/**
 * `Intl.NumberFormat.prototype.format`, яким він є в РАНТАЙМІ.
 *
 * ⛔ Не «обхід типізації заради зручності». `tsconfig.lib` — `ES2022`, а
 * рядковий аргумент `format()` описаний в `ES2023` (Intl v3): типи бачать
 * `format(value: number)`, рантайм приймає рядок. Підставити сюди
 * `Number(...)`, щоб догодити компіляторові, означало б втратити рівно той
 * знак, заради якого все це й робиться. Що рантайм таки приймає рядок і не
 * ріже знаків, доводить `__tests__/decimalDisplay.test.ts`, а не ця
 * декларація.
 */
interface StringAwareNumberFormat {
  format(value: string | number): string;
}

/**
 * Десяткове мовою інтерфейсу — З РЯДКА, без проходу через `Number`.
 *
 * Повертає `null`, якщо значення не десяткове: викликач сам вирішує, що
 * показати замість числа (ввід оператора як є, порожнє місце), і мовчазний
 * `''` позбавив би його цього вибору.
 *
 * ⛔ Хвостові нулі зрізає `normalizeDecimal` — і це та сама «нормалізація
 * ПОДАННЯ на клієнті», яку сервер свідомо не робить: нуль наприкінці буває
 * значущим у метрології, тож на ЗАПИСІ його чіпати не можна, а показувати
 * `5.0000000000` там, де оператор набрав `5`, — не можна тим паче.
 *
 * ⛔ `maximumFractionDigits` рахується з самого значення, а не береться з
 * дефолту `Intl`: дефолт — ТРИ знаки, тобто
 * `format('1234.1234567890123456')` дало б `1,234.123`. Мовчки.
 *
 * ⚠ `maxFractionDigits` — стеля ПОДАННЯ конкретного екрана, не політика цього
 * файлу. Вона тут тому, що інакше екран, якому стеля потрібна, заводить собі
 * копію цієї функції — і саме так їх стало три (`DataTable.tsx`,
 * `SnapshotRowsModal.tsx`). Дефолт `MaxIntlFractionDigits` означає «показати
 * все, що є у значенні»; екран, у якого своя стеля, називає її явно і
 * пояснює, звідки вона взялася, поруч із викликом.
 *
 * ⚠ `minFractionDigits` — доповнення нулями до формату комірки (сітка
 * документа, `scale` колонки: `1.5` при масштабі 4 → `1.5000`, як у Excel).
 * Лише ПОДАННЯ: на дріт, у редактор і в буфер нулі не йдуть. Дефолт 0 —
 * поведінка решти викликачів не змінюється.
 *
 * ⚠ `number` на вході теж приймається — колонки `Int`/`Lookup` їдуть числами й
 * далі. `String(number)` в експоненційному записі (`1e-7`) `normalizeDecimal`
 * свідомо відхиляє, тож такі значення повертаються як `null` і показуються
 * викликачем як є — рівно так, як їх малювала сітка до цього переходу.
 */
export function formatDecimal(
  value: unknown,
  lang?: Language,
  maxFractionDigits: number = MaxIntlFractionDigits,
  minFractionDigits = 0,
): string | null {
  const text =
    typeof value === 'string'
      ? value
      : typeof value === 'number' && Number.isFinite(value)
        ? String(value)
        : null;

  if (text === null) return null;

  const canonical = normalizeDecimal(text);
  if (canonical === null) return null;

  const dot = canonical.indexOf('.');
  const fractionDigits = dot === -1 ? 0 : canonical.length - dot - 1;

  // ⚠ Доповнення нулями (`minFractionDigits`) робить сам `Intl` над РЯДКОМ —
  // жодного `Number(...).toFixed()`, який з'їв би знаки за 17-ю значущою.
  // Мінімум не перевищує стелю: `Intl` кидає `RangeError`, коли min > max.
  const ceiling = Math.min(maxFractionDigits, MaxIntlFractionDigits);
  const minimum = Math.max(0, Math.min(Math.trunc(minFractionDigits), ceiling));

  const format = formatter(formatLocale(lang), {
    minimumFractionDigits: minimum,
    maximumFractionDigits: Math.max(minimum, Math.min(fractionDigits, ceiling)),
  }) as unknown as StringAwareNumberFormat;

  return format.format(canonical);
}
