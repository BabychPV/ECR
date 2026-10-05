import { describe, expect, it } from 'vitest';
import { normalizeUserDecimal } from '../userDecimal';

/**
 * Число, набране людиною в редакторі довідника, — за правилами серверного `CultureNumberReader`
 * (`tests/Ecr.Api.Tests/RegistryEntryNumberCultureHttpTests.cs`: `12,5` → 12.5 у будь-якій мові),
 * але без культури: неоднозначне `1,234` — відмова, а не вгадування.
 */
const read = (raw: string): string => {
  const result = normalizeUserDecimal(raw);
  return result.kind === 'number' ? result.text : result.kind;
};

describe('normalizeUserDecimal', () => {
  it('одна кома, після якої не рівно три цифри, — десятковий роздільник', () => {
    expect(read('12,5')).toBe('12.5');
    expect(read('-0,125')).toBe('-0.125');
    expect(read('1234,567')).toBe('1234.567');
    expect(read('0,125')).toBe('0.125');
    expect(read(',5')).toBe('0.5');
  });

  it('кома між 1–3 цифрами і рівно трьома — неоднозначна', () => {
    expect(read('1,234')).toBe('ambiguous');
    expect(read('-12,500')).toBe('ambiguous');
  });

  it('крапка — інваріантний запис; розряди комою, крапкою і пробілом', () => {
    expect(read('1.234')).toBe('1.234');
    expect(read('49.9999977539011')).toBe('49.9999977539011');
    expect(read('1,234,567')).toBe('1234567');
    expect(read('1,234.5')).toBe('1234.5');
    expect(read('1.234,5')).toBe('1234.5');
    expect(read('1 234,5')).toBe('1234.5');
    expect(read('1 000')).toBe('1000');
    expect(read('+1E-05')).toBe('1e-05');
  });

  it('не числа — як на сервері', () => {
    for (const raw of ['', 'abc', '1.', '1,', '1 2', '1,23,4', '1.2.3,4', '1,2,3.4,5', '-']) {
      expect(read(raw), raw).toBe('notNumber');
    }
  });
});
