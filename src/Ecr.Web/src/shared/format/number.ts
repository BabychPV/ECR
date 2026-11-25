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

/**
 * Найбільший `scale` колонки, який ще читається як ФОРМАТ комірки (доповнення
 * нулями, як у Excel: `0.00` → 2).
 *
 * ⛔ Більший масштаб — це точність СХОВИЩА, а не формат: імпорт Land дає
 * колонкам `decimal(38,16)` → `scale = 16`, виходи методологій — 12. Доповнення
 * до нього показувало `3.0000000000000000` у сітці, підсумках і журналі
 * (баг людини 02.11, документ 8 «Stationary Equipment»). Такі колонки
 * показуються найкоротшим точним записом (`3`); довший дріб не ріжеться.
 */
export const MaxPaddedScale = 6;

/**
 * Скільки знаків після коми доповнювати нулями для колонки з масштабом `scale`.
 * `0` — без доповнення: масштабу немає, він некоректний або це точність сховища
 * (`> MaxPaddedScale`). Одне правило для сітки, підсумків, журналу аркуша й
 * аудиту — щоб вони не розійшлися.
 */
export function displayPaddingOf(scale: number | null | undefined): number {
  if (typeof scale !== 'number' || !Number.isInteger(scale) || scale < 0) return 0;

  return scale > MaxPaddedScale ? 0 : scale;
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
  ...rest: [lang?: Language, maxFractionDigits?: number, minFractionDigits?: number]
): string | null {
  return formatDecimalExact(value, ...rest);
}

/** Значущих цифр у читабельному поданні за замовчуванням (людина 25.11: «6–8 значущих»). */
export const ReadableSignificantDigits = 7;

/**
 * Нижня межа «звичайного» запису: менші за модулем ненульові значення показуються науковим записом
 * (`4.013E-13`), а не рядком нулів.
 */
export const ReadableScientificBelow = '0.000001';

/** Читабельне подання десяткового: що показати, точне значення і чи було округлення. */
export interface ReadableDecimal {
  /** Що показати в клітинці/полі без фокуса. */
  readonly text: string;
  /** Повне точне значення мовою інтерфейсу (підказка, зчитувач екрана). */
  readonly exact: string;
  /** Чи `text` коротший за точне значення (тоді точне — у підказці й на фокусі). */
  readonly rounded: boolean;
}

/**
 * Десяткове для ЧИТАННЯ (людина 25.11, LIVE: «поля обрізають 0.0000000000004013…», «98.0835451945116000»).
 *
 * - хвостові нулі зрізано (`98.0835451945116000` → `98.08355`);
 * - не більше `significant` значущих цифр, але ЦІЛА частина не округлюється ніколи (`123456789.12` →
 *   `123,456,789`) — значення не може змінити порядок;
 * - тисячі групуються від п'яти цифр (`2031`, а не `2,031`: у числових стовпцях дозволів трапляються роки й коди);
 * - ненульові |x| < 0.000001 — науковим записом із 4 значущими (`4.013E-13`): порядок величини видно, а
 *   «< 0.000001» сховав би, що `4E-13` і `9E-7` різні на шість порядків, — для мол.% домішок це різні речі;
 * - точне значення (`exact`) — для підказки й фокуса: поле на фокусі показує збережений рядок повністю, і
 *   незмінене значення йде на сервер тим самим рядком (подання не торкається даних).
 *
 * ⛔ Усе — над РЯДКОМ (`Intl` приймає рядок, див. `formatDecimalExact`), без `Number(...)`.
 * `null` — значення не десяткове; викликач показує його як є.
 */
export function formatDecimalReadable(
  value: unknown,
  significant: number = ReadableSignificantDigits,
  lang?: Language,
): ReadableDecimal | null {
  const text =
    typeof value === 'string'
      ? value
      : typeof value === 'number' && Number.isFinite(value)
        ? expandExponent(String(value))
        : null;
  if (text === null) return null;
  const canonical = normalizeDecimal(text);
  if (canonical === null) return null;

  const exact = formatDecimalExact(canonical, lang, MaxIntlFractionDigits, 0, true) ?? canonical;
  const unsigned = canonical.startsWith('-') ? canonical.slice(1) : canonical;
  const [integer = '0', fraction = ''] = unsigned.split('.');
  const digits = Math.max(1, Math.trunc(significant));

  if (integer === '0' && fraction.length > 0) {
    const leadingZeros = /^0*/.exec(fraction)?.[0].length ?? 0;
    // Менше за 0.000001: перша значуща цифра далі за 6-й знак після коми.
    if (leadingZeros >= 6) {
      const format = formatter(formatLocale(lang), {
        notation: 'scientific',
        maximumSignificantDigits: Math.min(4, digits),
      }) as unknown as StringAwareNumberFormat;
      const scientific = format.format(canonical);
      return { text: scientific, exact, rounded: scientific !== exact };
    }

    const keep = Math.min(fraction.length, leadingZeros + digits, MaxIntlFractionDigits);
    const shown = formatDecimalExact(canonical, lang, keep, 0, true) ?? exact;
    return { text: shown, exact, rounded: keep < fraction.length };
  }

  const keep = Math.min(fraction.length, Math.max(0, digits - integer.length), MaxIntlFractionDigits);
  const shown = formatDecimalExact(canonical, lang, keep, 0, true) ?? exact;
  return { text: shown, exact, rounded: keep < fraction.length };
}

/**
 * Найкоротший запис `double` (`String(2.5e-7)` = `'2.5e-7'`) — звичайним десятковим (`'0.00000025'`), без нових
 * цифр: лише зсув коми. Значення, що приїхали числом (стовпці дозволів), інакше `normalizeDecimal` відхилив би.
 */
function expandExponent(text: string): string {
  const match = /^(-?)(\d)(?:\.(\d+))?e([+-]\d+)$/.exec(text);
  if (match === null) return text;
  const [, sign = '', head = '', tail = '', exp = '0'] = match;
  const digits = head + tail;
  const point = 1 + Number(exp);
  if (point <= 0) return `${sign}0.${'0'.repeat(-point)}${digits}`;
  if (point >= digits.length) return `${sign}${digits}${'0'.repeat(point - digits.length)}`;
  return `${sign}${digits.slice(0, point)}.${digits.slice(point)}`;
}

/** Точне подання (див. `formatDecimal`). */
function formatDecimalExact(
  value: unknown,
  lang?: Language,
  maxFractionDigits: number = MaxIntlFractionDigits,
  minFractionDigits = 0,
  groupFromFiveDigits = false,
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
    // ES2023 `useGrouping: 'min2'` (типи lib ES2022 знають лише boolean): тисячі групуються від п'яти цифр.
    ...(groupFromFiveDigits ? { useGrouping: 'min2' as unknown as boolean } : {}),
  }) as unknown as StringAwareNumberFormat;

  return format.format(canonical);
}
