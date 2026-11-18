import { describe, expect, it } from 'vitest';
import { outputsWithoutColumn } from '../scriptCoverage';
import { outline } from './fixtures';

const o = outline({
  outputs: [
    { code: 'A', unit: 't', perSubstance: false },
    { code: 'B', unit: 't', perSubstance: false },
    { code: 'C', unit: 't', perSubstance: false },
  ],
});

describe('виходи без колонки', () => {
  it('вихід без активних прив’язок і вихід, якого покриття ще не знає, — «немає колонки»', () => {
    const coverage = {
      outputs: [
        { code: 'A', bindings: [{ id: 1 }] },
        { code: 'B', bindings: [] },
      ],
    } as never;

    expect([...outputsWithoutColumn(o, coverage)].sort()).toEqual(['B', 'C']);
  });

  it('поки покриття чи схема не прочитані, нічого не позначається', () => {
    expect(outputsWithoutColumn(o, undefined).size).toBe(0);
    expect(outputsWithoutColumn(null, { outputs: [] }).size).toBe(0);
  });
});
