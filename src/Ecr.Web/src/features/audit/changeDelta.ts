/**
 * Різниця «стало − було» для числової зміни журналу (`UI-38`, макет `screen-document.js`: колонка «Δ»).
 *
 * ⛔ Рахується З РЯДКІВ, через `BigInt` у спільному масштабі, а не через `Number`: журнал зберігає
 * `decimal(34,16)` (`D-148`), і `Number('12345678901.1234567') - …` загубив би знаки за 17-ю значущою.
 * Δ, що бреше в останньому знаку, гірший за відсутній: журнал — доказ.
 *
 * Повертає канонічний десятковий рядок (`-1.5`, `0`, `12.0229`) або `null`, якщо будь-яке значення
 * не десяткове чи відсутнє (перше значення комірки, очищення) — тоді Δ не малюється (`D15-06`).
 */
const Decimal = /^([+-])?(\d+)(?:\.(\d+))?$/;

interface Scaled {
  readonly units: bigint;
  readonly scale: number;
}

function parse(value: string): Scaled | null {
  const match = Decimal.exec(value.trim());
  if (match === null) return null;

  const fraction = match[3] ?? '';
  const units = BigInt(`${match[2] ?? '0'}${fraction}`);

  return { units: match[1] === '-' ? -units : units, scale: fraction.length };
}

function rescale(value: Scaled, scale: number): bigint {
  return value.units * 10n ** BigInt(scale - value.scale);
}

export function decimalDelta(oldValue: string | null | undefined, newValue: string | null | undefined): string | null {
  if (oldValue === null || oldValue === undefined || newValue === null || newValue === undefined) return null;

  const before = parse(oldValue);
  const after = parse(newValue);
  if (before === null || after === null) return null;

  const scale = Math.max(before.scale, after.scale);
  const diff = rescale(after, scale) - rescale(before, scale);
  const negative = diff < 0n;
  const digits = (negative ? -diff : diff).toString().padStart(scale + 1, '0');
  const whole = digits.slice(0, digits.length - scale);
  const fraction = scale === 0 ? '' : digits.slice(digits.length - scale).replace(/0+$/, '');
  const text = fraction.length > 0 ? `${whole}.${fraction}` : whole;

  return negative && text !== '0' ? `-${text}` : text;
}
