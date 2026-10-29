import { describe, it, expect } from 'vitest';
import { auditValueText } from '@/features/audit/auditValue';
import { formatDecimal } from '@/shared/format';

/**
 * Значення журналів аудиту й журналу аркуша — за правилом показу колонки, а не у форматі сховища
 * (`aud.CellChange` зберігає число з масштабом `decimal(34,16)`: «437.6311000000000000»).
 *
 * ⛔ Мутація: повернути `return value` для числових типів — червоніє «хвостові нулі прибрані».
 */
describe('auditValueText', () => {
  it('Decimal: хвостові нулі сховища прибрані, значущі знаки — ні (масштаб 4, 2, 0)', () => {
    expect(auditValueText('437.6311000000000000', 'Decimal')).toBe('437.6311');
    expect(auditValueText('12.5000000000000000', 'Decimal')).toBe('12.5');
    expect(auditValueText('7.0000000000000000', 'Decimal')).toBe('7');
    expect(auditValueText('0.1200', 'Decimal')).toBe('0.12');
  });

  it('розряди — за локаллю інтерфейсу (en: кома; ru/kz: нерозривний пробіл)', () => {
    expect(auditValueText('1234567.8900000000000000', 'Decimal')).toBe('1,234,567.89');
    expect(formatDecimal('1234567.8900000000000000', 'ru')).toMatch(/^1\s234\s567,89$/u);
    expect(formatDecimal('1234567.8900000000000000', 'kz')).toMatch(/^1\s234\s567,89$/u);
  });

  it('Int, Formula і Calculated форматуються як число', () => {
    expect(auditValueText('1000', 'Int')).toBe('1,000');
    expect(auditValueText('3.1400000000000000', 'Formula')).toBe('3.14');
    expect(auditValueText('2.5000000000000000', 'Calculated')).toBe('2.5');
  });

  it('порожнє/нуль-значення: null — тире, порожній рядок лишається порожнім', () => {
    expect(auditValueText(null, 'Decimal')).toBe('—');
    expect(auditValueText(undefined, 'Decimal')).toBe('—');
    expect(auditValueText('', 'Decimal')).toBe('');
  });

  it('нечислова колонка не чіпається: рядок, довідник, невідомий тип', () => {
    expect(auditValueText('12.5000', 'String')).toBe('12.5000');
    expect(auditValueText('Power Pack OSRG/01/069', 'Lookup')).toBe('Power Pack OSRG/01/069');
    expect(auditValueText('000123', null)).toBe('000123');
  });

  it('нерозібране в числовій колонці показується як є (журнал — доказ)', () => {
    expect(auditValueText('n/a', 'Decimal')).toBe('n/a');
  });

  it('дата — за правилом показу дати, а не ISO сховища', () => {
    expect(auditValueText('2026-03-04T00:00:00', 'Date')).not.toBe('2026-03-04T00:00:00');
    expect(auditValueText('not a date', 'Date')).toBe('not a date');
  });
});
