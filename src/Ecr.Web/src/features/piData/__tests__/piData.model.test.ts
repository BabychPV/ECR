import { describe, expect, it } from 'vitest';
import { RangePresets, presetWindow } from '../rangePresets';
import { build, nearest } from '../SeriesChart';
import { exactDecimal } from '../RawPointsTable';
import type { RawSeriesView } from '../api';

/**
 * D-463: моделі «Даних з PI» без DOM — пресети вікна, сегменти графіка (розрив на порожніх кошиках), найближчий
 * кошик і показ десяткового рядка без хвостових нулів.
 */
describe('пресети вікна', () => {
  const now = new Date('2026-10-31T12:00:20.000Z');

  it('стабільні в межах хвилини і не довші за 400 діб (стеля сервера)', () => {
    for (const preset of RangePresets) {
      const window = presetWindow(preset, now);
      expect(presetWindow(preset, new Date('2026-10-31T12:00:50.000Z'))).toEqual(window);

      const days = (Date.parse(window.toUtc) - Date.parse(window.fromUtc)) / 86_400_000;
      expect(days).toBeGreaterThan(0);
      expect(days).toBeLessThanOrEqual(400);
    }
  });

  it('календарні роки — межі року UTC; поточний рік обрізано «зараз»', () => {
    expect(presetWindow('lastYear', now)).toEqual({
      fromUtc: '2025-01-01T00:00:00.000Z',
      toUtc: '2026-01-01T00:00:00.000Z',
    });
    expect(presetWindow('year', now)).toEqual({ fromUtc: '2026-01-01T00:00:00.000Z', toUtc: '2026-10-31T12:01:00.000Z' });
    expect(presetWindow('7d', now).fromUtc).toBe('2026-10-24T12:01:00.000Z');
  });
});

describe('модель графіка', () => {
  const series: RawSeriesView = {
    fromUtc: '2026-01-01T00:00:00Z',
    toUtc: '2026-01-01T10:00:00Z',
    bucketSeconds: 3600,
    totalPoints: 6,
    numericPoints: 5,
    buckets: [
      { startUtc: '2026-01-01T00:00:00Z', points: 2, min: '1', max: '3', avg: '2' },
      { startUtc: '2026-01-01T01:00:00Z', points: 1, min: '4', max: '4', avg: '4' },
      // 02:00–04:00 — прогалина: лінія мусить розірватися.
      { startUtc: '2026-01-01T04:00:00Z', points: 2, min: '-5', max: '10', avg: '2.5' },
      // Кошик без числових точок (лише текст) — на графік не йде.
      { startUtc: '2026-01-01T05:00:00Z', points: 1, min: null, max: null, avg: null },
    ],
  };

  it('розриває лінію на порожніх кошиках і пропускає нечислові', () => {
    const model = build(series);

    expect(model.buckets).toHaveLength(3);
    expect(model.segments.map((s) => s.length)).toEqual([2, 1]);
    expect(model.min).toBe(-5);
    expect(model.max).toBe(10);
    expect(model.yTicks).toHaveLength(5);
  });

  it('порожній ряд — осі не NaN', () => {
    const model = build({ ...series, buckets: [], totalPoints: 0, numericPoints: 0 });

    expect(model.min).toBe(0);
    expect(model.max).toBe(1);
    expect(model.segments).toEqual([]);
  });

  it('найближчий кошик за часом', () => {
    const model = build(series);

    expect(nearest(model.buckets, Date.parse('2026-01-01T03:50:00Z'))).toBe(2);
    expect(nearest(model.buckets, Date.parse('2026-01-01T00:10:00Z'))).toBe(0);
  });
});

describe('показ десяткового рядка', () => {
  it('прибирає лише хвостові нулі дробу', () => {
    expect(exactDecimal('12.5000000000000000')).toBe('12.5');
    expect(exactDecimal('-3.0000000000000000')).toBe('-3');
    expect(exactDecimal('100')).toBe('100');
    expect(exactDecimal('0.0000000000000001')).toBe('0.0000000000000001');
  });
});
