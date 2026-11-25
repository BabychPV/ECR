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
 * далі. `String(number)` в експоненційному записі (`1e-7`) розгортається в
 * звичайний дріб (`0.0000001`) зсувом коми — без нових цифр (картка
 * number-format-consistent, 25.11: експоненти на екрані не показуються).
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
 * Групування тисяч у поданні (картка number-format-consistent, 25.11: «75,435.96» поруч із «9363.267» в одному
 * стовпці читалося як два різні формати).
 *
 * - `'always'` — від 1 000 (`9,363.267`): стовпець таблиці, де є хоч одна ціла частина ≥ 10 000 (`columnGrouping`);
 * - `'never'` — ніде: поля вводу (набране не змішується з розділювачами) і стовпці без великих значень (рік/код `2031`);
 * - `'min2'` — від 10 000 (окреме значення поза стовпцем; поведінка до цієї картки).
 */
export type DecimalGrouping = 'always' | 'never' | 'min2';

/** Межа, від якої стовпець групується цілком: ціла частина з п'яти цифр. */
const ColumnGroupingFrom = 5;

/**
 * Правило групування для СТОВПЦЯ таблиці: `'always'`, якщо хоч одне значення (разом із підсумком, якщо він у тому ж
 * стовпці) має цілу частину ≥ 10 000 за модулем, інакше `'never'`. Так стовпець має розділювач тисяч скрізь або ніде,
 * і «скорочене» значення не відрізняється виглядом від нескороченого. Нечислові й порожні — не враховуються.
 */
export function columnGrouping(values: Iterable<unknown>): 'always' | 'never' {
  for (const value of values) {
    const plain = plainDecimal(value);
    if (plain === null) continue;
    const integer = (plain.startsWith('-') ? plain.slice(1) : plain).split('.')[0] ?? '';
    if (integer.length >= ColumnGroupingFrom) return 'always';
  }
  return 'never';
}

/**
 * Точний канонічний десятковий рядок БЕЗ експоненти й без локалі (`'0.0000008700'` → `'0.00000087'`, `8.7e-7` →
 * `'0.00000087'`); `null` — не десяткове. Для початкового тексту полів вводу й порівнянь: не округлює, не групує.
 */
export function plainDecimal(value: unknown): string | null {
  const text =
    typeof value === 'string'
      ? value
      : typeof value === 'number' && Number.isFinite(value)
        ? expandExponent(String(value))
        : null;
  return text === null ? null : normalizeDecimal(text);
}

/**
 * Ввід оператора в експоненті (`1e-9`, `1.397E-9`, `-4,013e-13`) → звичайний десятковий рядок (`0.000000001`) — лише
 * зсувом коми над цифрами, без `Number` (значення те саме, що набрано). Будь-який інший текст повертається як є: розбір
 * локалі й помилки вводу лишаються викликачеві.
 */
export function expandExponentInput(text: string): string {
  const match = /^([+-]?)(\d*)(?:[.,](\d*))?[eE]([+-]?\d{1,4})$/.exec(text.trim());
  if (match === null) return text;
  const [, sign = '', head = '', tail = '', exp = '0'] = match;
  if (head.length + tail.length === 0) return text;
  const digits = head + tail;
  const point = head.length + Number(exp);
  const shifted =
    point <= 0
      ? `0.${'0'.repeat(-point)}${digits}`
      : point >= digits.length
        ? `${digits}${'0'.repeat(point - digits.length)}`
        : `${digits.slice(0, point)}.${digits.slice(point)}`;
  const canonical = normalizeDecimal(shifted);
  if (canonical === null) return text;
  return sign === '-' && canonical !== '0' ? `-${canonical}` : canonical;
}

/**
 * Стеля дробу для ЧИТАБЕЛЬНОГО подання: ES2023 `Intl` приймає до 100 знаків (Chromium ≥ 106, Node ≥ 20 — обидва
 * середовища продукту). Потрібна, щоб `1e-25` не стало «0»: без експоненти крихітне значення показується повним дробом.
 */
const MaxReadableFractionDigits = 100;

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
 * - групування тисяч — за `grouping` (`DecimalGrouping`): у таблиці його задає СТОВПЕЦЬ (`columnGrouping`), у полі
 *   вводу — `'never'`; дефолт `'min2'` (від п'яти цифр) — для окремого значення поза стовпцем;
 * - БЕЗ експоненти (картка number-format-consistent, 25.11: «8.7E-7», «1.397E-9» поруч із «0.00004283738» читалися як
 *   різні формати): крихітне — звичайним дробом, `significant` значущих після провідних нулів, до точності сховища
 *   (`0.0000000000004013`);
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
  grouping: DecimalGrouping = 'min2',
): ReadableDecimal | null {
  const canonical = plainDecimal(value);
  if (canonical === null) return null;

  const unsigned = canonical.startsWith('-') ? canonical.slice(1) : canonical;
  const [integer = '0', fraction = ''] = unsigned.split('.');
  const cap = Math.min(fraction.length, MaxReadableFractionDigits);
  const exact = formatDecimalExact(canonical, lang, cap, 0, grouping, MaxReadableFractionDigits) ?? canonical;
  const digits = Math.max(1, Math.trunc(significant));

  const leadingZeros = integer === '0' ? (/^0*/.exec(fraction)?.[0].length ?? 0) : 0;
  const keep =
    integer === '0'
      ? Math.min(cap, leadingZeros + digits)
      : Math.min(cap, Math.max(0, digits - integer.length));
  const shown = formatDecimalExact(canonical, lang, keep, 0, grouping, MaxReadableFractionDigits) ?? exact;
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
  grouping?: DecimalGrouping,
  fractionCap: number = MaxIntlFractionDigits,
): string | null {
  // ⚠ Число в експоненті (`1e-7` — найкоротший запис double) розгортається зсувом коми, а не показується як `1e-7`
  // (картка number-format-consistent): нових цифр не з'являється.
  const canonical = plainDecimal(value);
  if (canonical === null) return null;

  const dot = canonical.indexOf('.');
  const fractionDigits = dot === -1 ? 0 : canonical.length - dot - 1;

  // ⚠ Доповнення нулями (`minFractionDigits`) робить сам `Intl` над РЯДКОМ —
  // жодного `Number(...).toFixed()`, який з'їв би знаки за 17-ю значущою.
  // Мінімум не перевищує стелю: `Intl` кидає `RangeError`, коли min > max.
  const ceiling = Math.min(maxFractionDigits, fractionCap);
  const minimum = Math.max(0, Math.min(Math.trunc(minFractionDigits), ceiling));

  const format = formatter(formatLocale(lang), {
    minimumFractionDigits: minimum,
    maximumFractionDigits: Math.max(minimum, Math.min(fractionDigits, ceiling)),
    // ES2023 `useGrouping: 'min2' | 'always' | false` (типи lib ES2022 знають лише boolean). Без `grouping` —
    // дефолт `Intl` (поведінка `formatDecimal` не змінюється).
    ...(grouping === undefined ? {} : { useGrouping: (grouping === 'never' ? false : grouping) as unknown as boolean }),
  }) as unknown as StringAwareNumberFormat;

  return format.format(canonical);
}
