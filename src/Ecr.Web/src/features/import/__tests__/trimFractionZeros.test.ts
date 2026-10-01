import { describe, it, expect } from 'vitest';
import { trimFractionZeros } from '../ImportPanel';

/** P3 (walk3 2026-10-01): «Review the import» показував 16 знаків дробу з decimal(25,16). */
describe('trimFractionZeros', () => {
  it.each([
    ['1.5000000000000000', '1.5'],
    ['2.3500000000000000', '2.35'],
    ['-0.1200000000000000', '-0.12'],
    ['7.0000000000000000', '7'],
    ['10.0500000000000000', '10.05'],
  ])('%s -> %s', (input, expected) => {
    expect(trimFractionZeros(input)).toBe(expected);
  });

  it.each(['100', '0', 'abc', '1.2.3', '100.50x', '', '1e5'])('«%s» лишається як є', (input) => {
    expect(trimFractionZeros(input)).toBe(input);
  });

  it('нуль у цілій частині не чіпає', () => {
    expect(trimFractionZeros('100.0000000000000001')).toBe('100.0000000000000001');
  });
});