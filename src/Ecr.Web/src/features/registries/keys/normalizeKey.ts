/**
 * Канонічний рядок складеного ключа довідника — клієнтський двійник
 * `src/Ecr.Domain/Services/RegistryKeyNormalizer.cs` (FEATURE-REGISTRY-TABLES
 * §4.2, `D-152`).
 *
 * ⛔ Обидві реалізації проганяються на ОДНІЙ фікстурі
 * `tests/Ecr.TestKit/Fixtures/registry-key-normalization.json`. Розбіжність
 * означала б, що сітка підсвічує дубль, якого сервер не бачить, або мовчить
 * там, де сервер відповість 409.
 *
 * ⚠ Хеш на клієнті навмисно НЕ рахується. Для пошуку дублів у сітці досить
 * порівняти канонічні рядки, а `crypto.subtle.digest` асинхронний і зробив би
 * асинхронною кожну перевірку рядка. Хеш — деталь сховища (`KeyHash
 * binary(32)`); що канонічний рядок клієнта хешується в той самий хеш,
 * перевіряє тест на фікстурі.
 */

/** Тип частини — ім'я члена `CellDataType` на сервері. */
export type KeyPartType = 'String' | 'Int' | 'Decimal' | 'Bool' | 'Date' | 'Lookup' | 'Unit';

/**
 * Значення частини. Числа — рядком або `bigint`: `decimal(34,16)` і `long`
 * не вміщаються в `number` без втрати знаків. Дата — `yyyy-MM-dd[T…]`.
 * `Lookup`/`Unit` — id цілі. `null`/`undefined` — значення немає.
 */
export type KeyPartValue = string | number | bigint | boolean | null | undefined;

export interface KeyPart {
  readonly type: KeyPartType;
  readonly value: KeyPartValue;
}

/** Роздільник частин — U+001F (unit separator), як на сервері. */
export const KEY_SEPARATOR = String.fromCharCode(0x1f);

/*
 * ⚠ «Пробільний» — за категоріями Unicode (Cc, Zs, Zl, Zp), а не `\s` чи
 * `trim()`: ті вважають пробільним U+FEFF і не вважають U+0085, тобто
 * розходяться з .NET. Роздільник U+001F — теж Cc, тож у значенні він стає
 * пробілом і не склеює дві частини в одну.
 */
const SPACE_RUN = /[\p{Cc}\p{Zs}\p{Zl}\p{Zp}]+/gu;
const EDGE_SPACES = /^[\p{Cc}\p{Zs}\p{Zl}\p{Zp}]+|[\p{Cc}\p{Zs}\p{Zl}\p{Zp}]+$/gu;

/*
 * `toUpperCase()` у JavaScript — ПОВНЕ відображення регістру (`ß` → `SS`,
 * `ﬁ` → `FI`), а `ToUpperInvariant` у .NET — просте, символ у символ. Тому
 * результат довший за один символ відкидається. `ı` → `ı`: .NET навмисно не
 * переводить безкрапкове i в `I` в інваріантній культурі.
 */
const DOTLESS_I = String.fromCharCode(0x131);
const INVARIANT_UPPER_EXCEPTIONS: ReadonlyMap<string, string> = new Map([[DOTLESS_I, DOTLESS_I]]);

const NUMBER = /^([+-]?)(\d*)(?:\.(\d*))?(?:[eE]([+-]?\d+))?$/;
const DATE = /^(\d{4})-(\d{2})-(\d{2})(?:T.*)?$/;
const INTEGER = /^-?\d+$/;
const MAX_EXPONENT = 1000;

/**
 * Канонічний рядок ключа: частини в порядку полів, з'єднані `KEY_SEPARATOR`.
 * `null`, якщо хоч одна частина порожня: такий рядок у перевірку ключа не
 * входить (`D-153`).
 */
export function canonicalKey(parts: readonly KeyPart[], ignoreCase = true): string | null {
  if (parts.length === 0) throw new RangeError('A key has at least one part.');

  const canonical: string[] = [];
  for (const part of parts) {
    const normalized = normalizeKeyPart(part.type, part.value, ignoreCase);
    if (normalized === null) return null;
    canonical.push(normalized);
  }
  return canonical.join(KEY_SEPARATOR);
}

/** Канонічна форма однієї частини: `тег:значення`; `null` — значення немає. */
export function normalizeKeyPart(
  type: KeyPartType,
  value: KeyPartValue,
  ignoreCase = true,
): string | null {
  if (value === null || value === undefined) return null;

  switch (type) {
    case 'String':
      return text(value, ignoreCase);
    case 'Int':
    case 'Decimal':
      return `N:${number(value)}`;
    case 'Bool':
      if (typeof value !== 'boolean') throw mismatch(type, value);
      return value ? 'B:1' : 'B:0';
    case 'Date':
      return `D:${date(value)}`;
    case 'Lookup':
      return `L:${id(value, type)}`;
    case 'Unit':
      return `U:${id(value, type)}`;
    default:
      throw new TypeError(`Field type ${String(type)} cannot be a key part.`);
  }
}

function text(value: KeyPartValue, ignoreCase: boolean): string | null {
  if (typeof value !== 'string') throw mismatch('String', value);

  // Порядок §4.2: NFC → обрізка → згортання → регістр.
  let result = value.normalize('NFC').replace(EDGE_SPACES, '');
  result = result.replace(SPACE_RUN, ' ');

  if (result.length === 0) return null;
  return `S:${ignoreCase ? upperInvariant(result) : result}`;
}

function upperInvariant(value: string): string {
  let result = '';
  for (const char of value) {
    const upper = INVARIANT_UPPER_EXCEPTIONS.get(char) ?? char.toUpperCase();
    result += [...upper].length === 1 ? upper : char;
  }
  return result;
}

/** Інваріантний десятковий без хвостових нулів і без експоненти; `-0` → `0`. */
function number(value: KeyPartValue): string {
  let literal: string;
  if (typeof value === 'string') literal = value;
  else if (typeof value === 'bigint') literal = value.toString();
  else if (typeof value === 'number' && Number.isFinite(value)) literal = String(value);
  else throw mismatch('Decimal', value);

  const match = NUMBER.exec(literal);
  const whole = match?.[2] ?? '';
  const fraction = match?.[3] ?? '';
  if (match === null || whole.length + fraction.length === 0) throw mismatch('Decimal', value);

  const exponent = Number(match[4] ?? '0');
  if (Math.abs(exponent) > MAX_EXPONENT) throw mismatch('Decimal', value);

  // Цифри без крапки і позиція крапки в них: рядкова арифметика, бо `number`
  // втратив би знаки `decimal(34,16)`.
  let digits = whole + fraction;
  let point = whole.length + exponent;
  const leading = digits.length - digits.replace(/^0+/, '').length;
  digits = digits.slice(leading);
  point -= leading;
  digits = digits.replace(/0+$/, '');
  if (digits.length === 0) return '0';

  let integer: string;
  let decimals: string;
  if (point <= 0) {
    integer = '0';
    decimals = '0'.repeat(-point) + digits;
  } else if (point >= digits.length) {
    integer = digits + '0'.repeat(point - digits.length);
    decimals = '';
  } else {
    integer = digits.slice(0, point);
    decimals = digits.slice(point);
  }

  const sign = match[1] === '-' ? '-' : '';
  return decimals.length > 0 ? `${sign}${integer}.${decimals}` : `${sign}${integer}`;
}

function date(value: KeyPartValue): string {
  const match = typeof value === 'string' ? DATE.exec(value) : null;
  if (match === null) throw mismatch('Date', value);

  const [, year = '', month = '', day = ''] = match;
  const y = Number(year);
  const m = Number(month);
  const d = Number(day);
  const leap = (y % 4 === 0 && y % 100 !== 0) || y % 400 === 0;
  const daysInMonth = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31][m - 1];
  if (daysInMonth === undefined || d < 1 || d > daysInMonth) throw mismatch('Date', value);

  return `${year}-${month}-${day}`;
}

function id(value: KeyPartValue, type: KeyPartType): string {
  if (typeof value === 'number' && Number.isSafeInteger(value)) return String(value);
  if (typeof value === 'bigint') return value.toString();
  if (typeof value === 'string' && INTEGER.test(value)) return BigInt(value).toString();
  throw mismatch(type, value);
}

function mismatch(type: KeyPartType, value: KeyPartValue): TypeError {
  return new TypeError(`A value ${String(value)} does not fit a ${type} key part.`);
}
