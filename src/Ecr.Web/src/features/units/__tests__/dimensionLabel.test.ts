import { describe, expect, it } from 'vitest';
import { dimensionLabel } from '@/features/units/dimensionLabel';

describe('dimensionLabel (UI RC9)', () => {
  it('відомий код — ключ каталога (без каталога тест видит ⟦ключ⟧, а не голый код)', () => {
    expect(dimensionLabel('Mass')).toBe('⟦units.dim.Mass⟧');
    expect(dimensionLabel('MassPerStdVolume')).toBe('⟦units.dim.MassPerStdVolume⟧');
  });

  it('невідомий код — як є, а не ⟦…⟧', () => {
    expect(dimensionLabel('Pressure')).toBe('Pressure');
  });
});
