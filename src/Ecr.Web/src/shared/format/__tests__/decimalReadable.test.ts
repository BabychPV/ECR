import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { DefaultLanguage, setLanguage } from '@/shared/i18n';
import { formatDecimalReadable } from '../number';

/**
 * Читабельне подання десяткового (людина 25.11, LIVE: обрізані мол.% і `98.0835451945116000` у редакторі складу газу,
 * обрізані числа в дозволах).
 *
 * ⛔ Доказ: пустити через `Number` → «точне значення без втрат» червоний. Науковий запис скасовано карткою
 * number-format-consistent (доказ — `numberFormatConsistent.test.ts`).
 */

/** Нерозривні пробіли ICU → звичайний. */
const norm = (text: string | undefined): string | undefined => text?.replace(/[   ]/g, ' ');

beforeEach(() => setLanguage('en'));
afterEach(() => {
  setLanguage(DefaultLanguage);
  localStorage.clear();
});

describe('formatDecimalReadable', () => {
  it('хвостові нулі зрізано, значущих не більше 7', () => {
    expect(formatDecimalReadable('98.0835451945116000')).toEqual({
      text: '98.08355',
      exact: '98.0835451945116',
      rounded: true,
    });
    expect(formatDecimalReadable('5.231988885816670')?.text).toBe('5.231989');
    expect(formatDecimalReadable('1.5000')).toEqual({ text: '1.5', exact: '1.5', rounded: false });
    expect(formatDecimalReadable('100')).toEqual({ text: '100', exact: '100', rounded: false });
  });

  it('точне значення без втрат — і через 20 значущих цифр, які Number зіпсував би', () => {
    expect(formatDecimalReadable('1234.1234567890123456')?.exact).toBe('1234.1234567890123456');
    // Тисячі групуються від п'яти цифр: рік/код у числовому стовпці не стає «2,031».
    expect(formatDecimalReadable('2031')?.text).toBe('2031');
    expect(formatDecimalReadable('12345.5')?.text).toBe('12,345.5');
  });

  it('ціла частина не округлюється ніколи', () => {
    expect(formatDecimalReadable('123456789.12')).toEqual({
      text: '123,456,789',
      exact: '123,456,789.12',
      rounded: true,
    });
  });

  it('значення менше одиниці — значущі цифри після провідних нулів', () => {
    expect(formatDecimalReadable('0.0169820827685202')?.text).toBe('0.01698208');
    expect(formatDecimalReadable('0.000012345678')?.text).toBe('0.00001234568');
  });

  // ✎ 25.11 (картка number-format-consistent): науковий запис скасовано — крихітні показуються звичайним дробом.
  it('крихітні значення — звичайним дробом, точне в exact', () => {
    const tiny = formatDecimalReadable('0.0000000000004013000');
    expect(tiny?.text).toBe('0.0000000000004013');
    expect(tiny?.exact).toBe('0.0000000000004013');
    expect(tiny?.rounded).toBe(false);
    expect(formatDecimalReadable('-0.00000098765')?.text).toBe('-0.00000098765');
    expect(formatDecimalReadable('0')?.text).toBe('0');
  });

  it('локаль: ru — кома і нерозривний пробіл тисяч', () => {
    setLanguage('ru');
    expect(formatDecimalReadable('1234.5678901')?.text).toBe('1234,568');
    expect(norm(formatDecimalReadable('12345.678901')?.text)).toBe('12 345,68');
    expect(formatDecimalReadable('0.0000000000004013')?.text).toBe('0,0000000000004013');
  });

  it('нечислове — null (викликач показує як є)', () => {
    expect(formatDecimalReadable('abc')).toBeNull();
    expect(formatDecimalReadable('')).toBeNull();
    expect(formatDecimalReadable(null)).toBeNull();
  });

  it('число на вході теж приймається', () => {
    expect(formatDecimalReadable(2.5)?.text).toBe('2.5');
    // Найкоротший запис double в експоненті (стовпці дозволів їдуть числами) — не губиться.
    expect(formatDecimalReadable(2.5e-7)).toEqual({ text: '0.00000025', exact: '0.00000025', rounded: false });
    expect(formatDecimalReadable(0.0000325)?.text).toBe('0.0000325');
    expect(formatDecimalReadable(1.5e21)?.text).toBe('1,500,000,000,000,000,000,000');
  });
});
