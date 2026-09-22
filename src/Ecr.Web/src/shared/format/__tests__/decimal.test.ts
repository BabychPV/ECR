import { describe, expect, it } from 'vitest';
import { decimalEquals, normalizeDecimal } from '../decimal';

/**
 * `decimal` у відповідях API їде рядком (`e470777a`), бо `JSON.parse` губить
 * 16-й знак беззворотно. Отже й порівнювати такі значення треба на рядках —
 * інакше зміна на сервері не дає нічого.
 */

describe('normalizeDecimal: масштаб колонки не робить число іншим', () => {
  it('хвостові нулі дробової частини значення не змінюють', () => {
    // ⚠ Саме так одиниця й приходить: `decimal(28,10)` друкується з усіма
    // десятьма знаками, і базова одиниця виглядає як `"1.0000000000"`.
    expect(normalizeDecimal('1')).toBe('1');
    expect(normalizeDecimal('1.0')).toBe('1');
    expect(normalizeDecimal('1.0000000000')).toBe('1');
  });

  it('нуль — це нуль у будь-якому записі, разом із «мінус нуль»', () => {
    for (const zero of ['0', '0.0', '-0', '-0.000', '+0', '.0', '00']) {
      expect(normalizeDecimal(zero), zero).toBe('0');
    }
  });

  it('провідні нулі, плюс і пробіли обрізаються, значущі знаки — ні', () => {
    expect(normalizeDecimal(' +01.2300 ')).toBe('1.23');
    expect(normalizeDecimal('-000.500')).toBe('-0.5');
    expect(normalizeDecimal('0.1000000000000000000001')).toBe('0.1000000000000000000001');
  });

  it('не число — `null`, а не здогад', () => {
    // ⚠ `1e3` теж: `decimal.ToString` з інваріантною культурою експоненти не
    // друкує, тож такий рядок прийшов не з контракту.
    for (const junk of ['', '   ', '-', '.', 'abc', '1e3', '1,5', '1.2.3']) {
      expect(normalizeDecimal(junk), junk).toBeNull();
    }
  });
});

describe('decimalEquals: там, де `Number` бреше', () => {
  it('16-й знак і далі зберігається — `Number` тут дав би рівність', () => {
    const long = '0.1000000000000000000001';

    // ⛔ Доказ, що перевірка потрібна: через `Number` ці два рядки — ОДНЕ
    // число, тобто `Number(a) === Number(b)` мовчки сказав би «однакові».
    expect(Number(long) === Number('0.1')).toBe(true);
    expect(decimalEquals(long, '0.1')).toBe(false);
  });

  it('те саме число в різному масштабі — рівне', () => {
    expect(decimalEquals('1.0000000000', '1')).toBe(true);
    expect(decimalEquals('0.0000000000', '0')).toBe(true);
    expect(decimalEquals('-0', '0')).toBe(true);
    expect(decimalEquals(' 2.50 ', '2.5')).toBe(true);
  });

  it('знак і величина розрізняються', () => {
    expect(decimalEquals('-1.5', '1.5')).toBe(false);
    expect(decimalEquals('1.5', '15')).toBe(false);
    expect(decimalEquals('1.0000000001', '1')).toBe(false);
  });

  it('нерозпізнане не дорівнює навіть саме собі', () => {
    // ⛔ Інакше пара однакового сміття читалася б як «значення збіглися».
    expect(decimalEquals('abc', 'abc')).toBe(false);
    expect(decimalEquals('', '')).toBe(false);
  });
});
