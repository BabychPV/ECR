import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { DefaultLanguage, setLanguage } from '@/shared/i18n';
import type { ColumnDto } from '@/api/types';
import { cellDisplay, cellText, displayFormatDigitsOf, editorValueOf } from '../cellValue';

function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    code: 'v',
    dataType: 'Decimal',
    defaultValue: null,
    displayFormat: null,
    header: 'V',
    id: 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    lookupRegistryDefId: null,
    ordinal: 0,
    precision: 28,
    scale: 12,
    unitId: null,
    unitSymbol: null,
    ...overrides,
  };
}

describe('формат відображення колонки (Land F3)', () => {
  beforeEach(() => setLanguage('en'));
  afterEach(() => setLanguage(DefaultLanguage));

  it('розпізнає Excel- і .NET-формати фіксованих знаків', () => {
    expect(displayFormatDigitsOf('0')).toBe(0);
    expect(displayFormatDigitsOf('0.00')).toBe(2);
    expect(displayFormatDigitsOf('#,##0.000')).toBe(3);
    expect(displayFormatDigitsOf('N2')).toBe(2);
    expect(displayFormatDigitsOf('f4')).toBe(4);
    expect(displayFormatDigitsOf('0.0%')).toBeNull();
    expect(displayFormatDigitsOf('yyyy-MM-dd')).toBeNull();
    expect(displayFormatDigitsOf('')).toBeNull();
    expect(displayFormatDigitsOf(null)).toBeNull();
  });

  it('displayFormat має перевагу над scale: 5.000000000000 при 0.00 → 5.00', () => {
    expect(cellDisplay('5.000000000000', column({ displayFormat: '0.00' }))).toBe('5.00');
    expect(cellDisplay('1234.5678', column({ displayFormat: 'N2' }))).toBe('1,234.57');
  });

  it('без формату лишається доповнення до scale (попередня поведінка)', () => {
    expect(cellDisplay('5', column({ scale: 4 }))).toBe('5.0000');
  });

  it('редактор і буфер не округлюються форматом', () => {
    const c = column({ displayFormat: '0.00' });
    expect(editorValueOf('1.234567000000', c)).toBe('1.234567');
    expect(cellText('1.234567000000')).toBe('1.234567');
  });
});
