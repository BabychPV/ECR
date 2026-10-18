import { describe, expect, it } from 'vitest';
import { dimensionLabel } from '@/features/units/dimensionLabel';

describe('dimensionLabel (UI RC9)', () => {
  it('відомий код — ключ каталога (без каталога тест видит ⟦ключ⟧, а не голый код)', () => {
    expect(dimensionLabel('Mass')).toBe('⟦units.dim.Mass⟧');
    expect(dimensionLabel('MassPerStdVolume')).toBe('⟦units.dim.MassPerStdVolume⟧');
  });

  it('розмірності 20–23 (CL-20, B-6) мають ключі каталога, а не голий англійський код', () => {
    for (const code of ['VolumeFlow', 'Power', 'Frequency', 'Pressure']) {
      expect(dimensionLabel(code)).toBe(`⟦units.dim.${code}⟧`);
    }
  });

  it('невідомий код — як є, а не ⟦…⟧', () => {
    expect(dimensionLabel('SomethingNew')).toBe('SomethingNew');
  });
});
