import { describe, expect, it } from 'vitest';
import type { ColumnDto } from '@/api/types';
import { cellDisplay } from '../cellValue';

/**
 * Текстовий результат Formula/Calculated-колонки (міст, «No - Нет») показується
 * текстом як є; числовий — форматується як і раніше (AN-125, сторож шляху показу).
 */

function column(overrides: Partial<ColumnDto>): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'C1',
    dataType: 'Formula',
    ordinal: 1,
    isReadOnly: true,
    isRequired: false,
    isRequiredByMethodology: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: null,
    unitId: null,
    unitSymbol: null,
    ...overrides,
  };
}

describe('cellDisplay для Formula/Calculated', () => {
  it.each(['Formula', 'Calculated'] as const)('%s: текстовий результат — як є', (dataType) => {
    expect(cellDisplay('No - Нет', column({ dataType }))).toBe('No - Нет');
  });

  it('числовий результат лишається форматованим (scale — нижня межа)', () => {
    const shown = cellDisplay('12.5', column({ scale: 2 }));

    expect(shown).toMatch(/^12[.,]50$/);
  });

  it('порожнє — порожній рядок', () => {
    expect(cellDisplay(null, column({}))).toBe('');
  });
});
