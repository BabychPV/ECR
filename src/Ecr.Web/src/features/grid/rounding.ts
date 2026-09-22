import type { ColumnDto } from '@/api/types';

/**
 * Округлення при вставці (`ФВ-9.16c`, `D-116`).
 *
 * ⛔ Округлює **лише вставка**. Ручне введення і API зайвого знака не
 * отримують: там це помилка автора, і сервер відхиляє її кодом
 * `ECR-CELL-0422`. У вставці з Excel зайві знаки трапляються постійно —
 * аркуш замовника рахує у своїх формулах із повною точністю, — і відхилення
 * сотень комірок зробило б **головний шлях введення** непридатним.
 *
 * ⚠ Тому округлення відбувається на КЛІЄНТІ, до надсилання. Сервер лишається
 * суворим і нічого не округлює мовчки: правило «мовчки округлювати не можна
 * ніде» виконується тим, що користувач бачить позначку і лічильник одразу.
 *
 * ⛔ Увесь шлях округлення — РЯДКОВИЙ: ані `10 ** scale`, ані `Number(...)`.
 * До 2026-09-20 тут множили на `10 ** scale`, і при `scale = 16` множник
 * (10^16) більший за `Number.MAX_SAFE_INTEGER` (9 007 199 254 740 991) —
 * зсунуте значення втрачало цілі одиниці, тож округлення мовчки давало інше
 * число. `decimal(25,16)` — не екзотика: саме в такому масштабі оголошені
 * проміжні колонки газового складу (`NumericPolicy.OutputScale`, коментар).
 */

/** Комірка, значення якої змінилося округленням. */
export interface RoundedCell {
  rowKey: string;
  columnCode: string;
  /** Те, що було в буфері. */
  original: string;
  /**
   * Те, що піде на сервер.
   *
   * ✎ 2026-09-21: борг закрито. Тут стояло ЧИСЛО з поясненням «контракт
   * десяткове рядком ще не приймає», і `DocumentGrid` робив `Number(...)` на
   * межі відправлення — тобто весь рядковий шлях округлення закінчувався
   * поверненням у `double` за один крок до мережі. Після `e470777a` сервер
   * приймає й віддає `decimal` рядком (`PatchCell.value` — `unknown`), тож
   * рядок їде як є, і другої точки втрати точності більше немає.
   */
  applied: string;
}

/**
 * Округлює значення до масштабу колонки.
 *
 * Вхід — текст, **як він прийшов із буфера**; вихід — рядок округленого
 * значення або `null`.
 *
 * ⛔ Не `number`: до числа вже дійшов би `Number(text)`, а `double` тримає
 * ~15–17 значущих цифр — округлювати його до 16 знаків після коми безглуздо.
 * Рядок дозволяє показати рівно те, що ввів користувач.
 *
 * ⚠ Повертає `null`, коли округлювати нічого: колонка не десяткова, масштаб
 * не заданий, значення не число або воно вже вкладається в масштаб. `null`
 * означає саме «нічого не змінилося», і викликач не ставить позначки —
 * позначка лише на ЗМІНЕНІ комірки, інакше лічильник втрачає сенс.
 */
export function roundToScale(text: string, column: ColumnDto): string | null {
  if (column.dataType !== 'Decimal') return null;

  const scale = column.scale;
  if (scale === null || scale === undefined) return null;

  return roundDecimalText(text, scale);
}

/**
 * Ядро `roundToScale` без прив'язки до колонки: текст → округлений до `scale`
 * знаків (AwayFromZero), суто рядково. `null` — вже в масштабі або не число.
 *
 * ⚠ Спільне для вводу (`roundToScale`, лише `Decimal`) і для ПОКАЗУ
 * (`cellDisplay`, усі числові типи зі `scale`) — одне правило, не дві копії.
 */
export function roundDecimalText(text: string, scale: number): string | null {
  if (!Number.isInteger(scale) || scale < 0) return null;

  const parts = parseDecimal(text);
  if (parts === null) return null;

  // ⚠ Дробова частина вже без хвостових нулів, тож «знаків не більше за
  // масштаб» — це і є «вже в масштабі»: `12.3400` при масштабі 2 не змінюється
  // і позначки не отримує. Інакше половина вставки з Excel, який охоче дописує
  // нулі до формату колонки, світилася б як «округлено».
  if (parts.frac.length <= scale) return null;

  return roundHalfAwayFromZero(parts, scale);
}

/** Розібране десяткове: знак і дві групи цифр, точка — між ними. */
interface DecimalParts {
  /** `'-'` або порожній рядок. */
  sign: string;
  /** Ціла частина без ведучих нулів, щонайменше `'0'`. */
  int: string;
  /** Дробова частина без хвостових нулів, можливо порожня. */
  frac: string;
}

/**
 * Найбільший показник степеня, який розгортається в позиційний запис.
 *
 * ⚠ Не захист від «дивного вводу», а захист пам'яті: `1e+1000000` розгорнувся
 * б у мільйон нулів. 400 із запасом перекриває діапазон `double` (~1e308), за
 * яким `parseNumber` (`clipboard.ts`) однаково віддає `null`.
 */
const MaxExponent = 400;

/**
 * Розбирає текст буфера в десяткове без втрати знаків.
 *
 * ⚠ Нормалізація — дзеркало `parseNumber` (`clipboard.ts`), через яку той
 * самий текст проходить у `coerce`: пробіли (зокрема нерозривні — їх покриває
 * `\s`) відкидаються, а самотня кома вважається десятковим роздільником.
 * Розійтися ці двоє не мають права: значення, яке `coerce` вважає числом, а
 * округлення — ні, поїхало б на сервер неокругленим і отримало б
 * `ECR-CELL-0422` на головному шляху введення.
 *
 * ⛔ Тому `'1,5'` тут НЕ відхиляється (уточнення до початкової постановки, де
 * кома була в переліку «не число»): аркуш в uk-UA пише десяткову саме комою,
 * і `parseNumber` це вже приймає. Відхиляються `'abc'`, порожній рядок,
 * `'1.2,3'`, `'1.'`, `'.5'`, `Infinity`/`NaN` — усе, що й `parseNumber`.
 *
 * ⚠ Експоненційний запис ПІДТРИМАНО: Excel показує великі й малі числа саме
 * так, `parseNumber` його приймає, а мовчки лишити такі комірки
 * неокругленими означало б відхилення на сервері. Показник розгортається
 * зсувом десяткової точки — теж суто рядково.
 */
function parseDecimal(text: string): DecimalParts | null {
  const stripped = text.replace(/\s/g, '');
  if (stripped.length === 0) return null;

  const normalized =
    stripped.includes(',') && !stripped.includes('.') ? stripped.replace(',', '.') : stripped;

  const match = /^([+-]?)(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$/.exec(normalized);
  if (match === null) return null;

  const digits = (match[2] ?? '') + (match[3] ?? '');
  const exponent = match[4] === undefined ? 0 : Number(match[4]);
  if (!Number.isInteger(exponent) || Math.abs(exponent) > MaxExponent) return null;

  // Позиція десяткової точки в `digits` після зсуву на показник.
  const point = (match[2] ?? '').length + exponent;

  let int: string;
  let frac: string;

  if (point <= 0) {
    int = '0';
    frac = '0'.repeat(-point) + digits;
  } else if (point >= digits.length) {
    int = digits + '0'.repeat(point - digits.length);
    frac = '';
  } else {
    int = digits.slice(0, point);
    frac = digits.slice(point);
  }

  return {
    sign: match[1] === '-' ? '-' : '',
    int: int.replace(/^0+(?=\d)/, ''),
    frac: frac.replace(/0+$/, ''),
  };
}

/**
 * Ввід оператора чи буфера → десятковий ЗАПИС, який приймає контракт.
 *
 * ⛔ Заведено тут, а не поруч із `normalizeDecimal` (`shared/format/decimal.ts`),
 * і це межа відповідальності, а не зручність. Той канон описує ДРІТ: сервер
 * друкує `decimal` інваріантною культурою, тобто ні коми, ні розрядних
 * пробілів, ні експоненти там не буває — і він їх свідомо відхиляє. А в буфері
 * Excel усі три є щодня. Отже розгортає їх той, хто ввід і читає, — цей файл,
 * у якому потрібний розбір уже стояв для округлення.
 *
 * ⚠ Результат `parseDecimal` уже канонічний (ведучі нулі цілої й хвостові нулі
 * дробу зрізані), тож `normalizeDecimal` над ним був би тотожним: ці дві
 * функції зобов'язані давати той самий канон, і це твердження в тесті, а не
 * домовленість.
 *
 * @returns Десятковий запис без експоненти; `null` — вхід не є числом.
 */
export function decimalTextOf(text: string): string | null {
  const parts = parseDecimal(text);

  return parts === null ? null : magnitudeOf(parts);
}

/** Знак і цифри в один рядок; `-0` зводиться до `0`. */
function magnitudeOf(parts: DecimalParts): string {
  const magnitude = parts.frac.length > 0 ? `${parts.int}.${parts.frac}` : parts.int;

  // ⚠ `-0.4` при масштабі 0 дає `0`, а не `-0`: `decimal` від'ємного нуля не
  // має, тож знак тут лише зашумив би і позначку «округлено», і порівняння.
  return magnitude === '0' ? '0' : parts.sign + magnitude;
}

/**
 * Округлення «половина від нуля» — те саме правило, що й на сервері
 * (`ColumnDef.Validate`: `decimal.Round(dec, scale, MidpointRounding.AwayFromZero)`).
 *
 * ⛔ НЕ «половина вгору» і не `Math.round`: обидва округлюють −2.5 до −2
 * (до +∞), а .NET — до −3. Різниця в один знак на від'ємному значенні означає,
 * що клієнт надішле число, яке сервер вважатиме неокругленим, і вставка
 * відхилиться там, де мала пройти. Тому вирішує ПЕРША відкинута цифра
 * модуля, а знак приписується назад уже після.
 *
 * ⚠ Ніякого `Number.EPSILON`: поправка потрібна була рівно тому, що
 * `1.005 * 100` у подвійній точності дає `100.49999999999999`. Тут множення
 * немає — цифри беруться з тексту як є.
 */
function roundHalfAwayFromZero(parts: DecimalParts, scale: number): string {
  const kept = parts.frac.slice(0, scale);
  const dropped = parts.frac.slice(scale);

  const digits = dropped.charAt(0) >= '5'
    ? increment(parts.int + kept)
    : parts.int + kept;

  const cut = digits.length - scale;

  return magnitudeOf({
    sign: parts.sign,
    int: digits.slice(0, cut).replace(/^0+(?=\d)/, ''),
    frac: digits.slice(cut).replace(/0+$/, ''),
  });
}

/** Додає одиницю до рядка цифр; `'999'` → `'1000'`. */
function increment(digits: string): string {
  const out = digits.split('');

  for (let i = out.length - 1; i >= 0; i--) {
    const digit = out[i] ?? '0';

    if (digit === '9') {
      out[i] = '0';
      continue;
    }

    out[i] = String(Number(digit) + 1);
    return out.join('');
  }

  return `1${out.join('')}`;
}
