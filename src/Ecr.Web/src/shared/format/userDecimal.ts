/**
 * Число, НАБРАНЕ людиною (або вставлене з Excel) у редакторі довідника, → інваріантний запис
 * для сервера (`12,5` → `12.5`, `1 234,5` → `1234.5`).
 *
 * ⚠ Правила — ті самі, що в серверному `CultureNumberReader` (`Ecr.Application/Localization`),
 * але без культури: клієнт шле вже інваріантний рядок, і сервер читає його однаково в будь-якій
 * мові. Тому там, де сервер вирішив би культурою (`1,234` — розряди в en-US, десятковий у ru),
 * тут — `ambiguous`: людину просять записати однозначно, а не вгадують за неї.
 *
 * Пробіли (зокрема нерозривні) — лише розряди: `1 234 567`; `1 2` — не число.
 * 1. Є і крапка, і кома — десятковий той, що ОСТАННІЙ і лише раз; другий — коректні розряди.
 * 2. Один роздільник кілька разів — розряди (`1,234,567`).
 * 3. Одна кома, яка НЕ може бути розрядом (після неї не рівно три цифри, або перед нею понад три
 *    цифри чи ведучий нуль) — десяткова (`12,5`, `1234,5`, `0,125`).
 * 4. Одна кома між 1–3 цифрами і рівно трьома — `ambiguous` (`1,234`).
 * 5. Одна крапка — завжди десяткова (інваріантний запис, так показує й копіює сітка).
 * `.5`/`,5` — 0.5; `1.`/`1,` — не число, як і на сервері.
 */
export type UserDecimal =
  | { readonly kind: 'number'; readonly text: string }
  | { readonly kind: 'ambiguous' }
  | { readonly kind: 'notNumber' };

const NotNumber: UserDecimal = { kind: 'notNumber' };

/** Цілі з розрядами `separator`: перша група 1–3 цифри, далі рівно по три. */
function isGrouped(text: string, separator: string): boolean {
  const [head, ...rest] = text.split(separator);
  return head !== undefined && /^\d{1,3}$/.test(head) && rest.length > 0 && rest.every((group) => /^\d{3}$/.test(group));
}

export function normalizeUserDecimal(raw: string): UserDecimal {
  const trimmed = raw.trim();
  if (/\s/.test(trimmed) && !/^[+-]?\d{1,3}(?:\s\d{3})+(?:[.,]\d+)?(?:[eE][+-]?\d+)?$/.test(trimmed)) return NotNumber;

  const match = /^([+-]?)([\d.,]+)(?:[eE]([+-]?\d+))?$/.exec(trimmed.replace(/\s/g, ''));
  if (match === null) return NotNumber;

  const sign = match[1] === '-' ? '-' : '';
  const mantissa = match[2] ?? '';
  const exponent = match[3] === undefined ? '' : `e${match[3]}`;
  const number = (int: string, frac: string): UserDecimal => ({
    kind: 'number',
    text: `${sign}${int}${frac.length > 0 ? `.${frac}` : ''}${exponent}`,
  });

  const lead = /^[.,](\d+)$/.exec(mantissa);
  if (lead !== null) return number('0', lead[1] ?? '');
  if (!/^\d/.test(mantissa) || !/\d$/.test(mantissa)) return NotNumber;

  const commas = mantissa.split(',').length - 1;
  const dots = mantissa.split('.').length - 1;
  if (commas === 0 && dots === 0) return number(mantissa, '');

  if (commas > 0 && dots > 0) {
    const last = Math.max(mantissa.lastIndexOf(','), mantissa.lastIndexOf('.'));
    const decimal = mantissa.charAt(last);
    const group = decimal === ',' ? '.' : ',';
    if ((decimal === ',' ? commas : dots) !== 1) return NotNumber;
    const int = mantissa.slice(0, last);
    return isGrouped(int, group) ? number(int.split(group).join(''), mantissa.slice(last + 1)) : NotNumber;
  }

  const separator = commas > 0 ? ',' : '.';
  if (commas + dots > 1) {
    return isGrouped(mantissa, separator) ? number(mantissa.split(separator).join(''), '') : NotNumber;
  }

  const at = mantissa.indexOf(separator);
  const before = mantissa.slice(0, at);
  const after = mantissa.slice(at + 1);
  const canBeGroup = after.length === 3 && before.length >= 1 && before.length <= 3 && !before.startsWith('0');

  return separator === ',' && canBeGroup ? { kind: 'ambiguous' } : number(before, after);
}
