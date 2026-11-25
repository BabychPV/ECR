import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { DefaultLanguage, setLanguage } from '@/shared/i18n';
import { columnGrouping, expandExponentInput, formatDecimal, formatDecimalReadable, plainDecimal } from '../number';

/**
 * Один вигляд чисел у стовпці й полі (картка number-format-consistent, знахідка UI-проходу live b13 25.11):
 * «75,435.96» поруч із «9363.267» в одному стовпці «Emission limits», «8.7E-7» / «1.397E-9» у таблицях і полях.
 *
 * AC-1: без експоненти (до точності сховища), групування однакове в стовпці незалежно від того, скорочено число чи ні.
 * ⛔ Доказ: повернути гілку наукового запису у `formatDecimalReadable` → «без експоненти» червоний; `columnGrouping`
 * повертати 'min2' → «стовпець групується однаково» червоний.
 */

const norm = (text: string | undefined): string | undefined => text?.replace(/[   ]/g, ' ');

beforeEach(() => setLanguage('en'));
afterEach(() => {
  setLanguage(DefaultLanguage);
  localStorage.clear();
});

describe('AC-1: без експоненти', () => {
  it('крихітні десяткові з рядка — звичайним дробом до точності сховища, точне в exact', () => {
    expect(formatDecimalReadable('0.0000000000004013000')).toEqual({
      text: '0.0000000000004013',
      exact: '0.0000000000004013',
      rounded: false,
    });
    expect(formatDecimalReadable('0.000000001397')?.text).toBe('0.000000001397');
    expect(formatDecimalReadable('0.00004283738')?.text).toBe('0.00004283738');
  });

  it('довгий хвіст — 7 значущих після провідних нулів, повне значення в підказці', () => {
    const shown = formatDecimalReadable('0.0000000013971234567');
    expect(shown?.text).toBe('0.000000001397123');
    expect(shown?.exact).toBe('0.0000000013971234567');
    expect(shown?.rounded).toBe(true);
  });

  it('число (double) в експоненті — розгорнуто без нових цифр', () => {
    expect(formatDecimalReadable(8.7e-7)).toEqual({ text: '0.00000087', exact: '0.00000087', rounded: false });
    expect(formatDecimalReadable(-9.8765e-7)?.text).toBe('-0.00000098765');
    expect(formatDecimalReadable(1e-25)?.text).toBe('0.0000000000000000000000001');
  });

  it('жоден вивід не містить E — і точний formatDecimal для числа теж', () => {
    for (const value of [8.7e-7, 1.397e-9, 4.013e-13, '0.0000000000004013', 1e-25]) {
      expect(formatDecimalReadable(value)?.text).not.toMatch(/e/i);
      expect(formatDecimalReadable(value)?.exact).not.toMatch(/e/i);
    }
    expect(formatDecimal(1e-7)).toBe('0.0000001');
  });

  it('ru: кома, без експоненти', () => {
    setLanguage('ru');
    expect(formatDecimalReadable('0.0000000000004013')?.text).toBe('0,0000000000004013');
  });
});

describe('AC-1: групування одне на стовпець', () => {
  it('стовпець, де є ціла частина ≥ 10 000, — групуються всі значення від 1 000, скорочені й ні', () => {
    const column = ['75435.9612345', '9363.267', '0.00000087', '2031'];
    const grouping = columnGrouping(column);
    expect(grouping).toBe('always');
    expect(column.map((v) => formatDecimalReadable(v, undefined, undefined, grouping)?.text)).toEqual([
      '75,435.96',
      '9,363.267',
      '0.00000087',
      '2,031',
    ]);
    // Підказка — тим самим правилом.
    expect(formatDecimalReadable('75435.9612345', undefined, undefined, grouping)?.exact).toBe('75,435.9612345');
  });

  it('стовпець без значень ≥ 10 000 — ніде (рік/код лишається «2031»)', () => {
    const column = [9363.267, 2031, '-9999.5', null, '', 'abc'];
    const grouping = columnGrouping(column);
    expect(grouping).toBe('never');
    expect(formatDecimalReadable(9363.267, undefined, undefined, grouping)?.text).toBe('9363.267');
    expect(formatDecimalReadable('2031', undefined, undefined, grouping)?.text).toBe('2031');
  });

  it('від’ємні й числа рахуються за модулем цілої частини', () => {
    expect(columnGrouping(['-10000'])).toBe('always');
    expect(columnGrouping([12345.6])).toBe('always');
    expect(columnGrouping([1.5e21])).toBe('always');
    expect(columnGrouping([])).toBe('never');
  });

  it('never — без розділювача навіть для великих', () => {
    expect(formatDecimalReadable('123456789.12', undefined, undefined, 'never')?.text).toBe('123456789');
  });

  it('ru, always: нерозривний пробіл і для чотирьох цифр', () => {
    setLanguage('ru');
    expect(norm(formatDecimalReadable('9363.267', undefined, undefined, 'always')?.text)).toBe('9 363,267');
  });
});

describe('AC-2: розбір вводу з експонентою', () => {
  it('1e-9 і подібні — звичайним десятковим, без Number', () => {
    expect(expandExponentInput('1e-9')).toBe('0.000000001');
    expect(expandExponentInput('1E-9')).toBe('0.000000001');
    expect(expandExponentInput(' 1.397e-9 ')).toBe('0.000000001397');
    expect(expandExponentInput('-4,013E-13')).toBe('-0.0000000000004013');
    expect(expandExponentInput('+2.5e3')).toBe('2500');
    expect(expandExponentInput('12.5e-1')).toBe('1.25');
    expect(expandExponentInput('.5e1')).toBe('5');
  });

  it('не експонента — повертається як є', () => {
    for (const text of ['1.5', '1,2x', '', 'e-9', '1e', '1e-', 'abc', '1,234.5e2']) expect(expandExponentInput(text)).toBe(text);
  });

  it('plainDecimal — точний канонічний рядок без експоненти й групування', () => {
    expect(plainDecimal('0.0000008700000000')).toBe('0.00000087');
    expect(plainDecimal(8.7e-7)).toBe('0.00000087');
    expect(plainDecimal('75435.9612345000')).toBe('75435.9612345');
    expect(plainDecimal('abc')).toBeNull();
  });
});
