import { describe, expect, it } from 'vitest';
import { priorScope } from '../import/scopeModel';
import { scopeFields } from './airFixtures';

const row = (regionEntryId: number | null, areaEntryId: number | null, region: string | null = null, area: string | null = null) => ({
  region,
  regionEntryId,
  area,
  areaEntryId,
});

/** Re-import prefill: rows under exactly one region/area start the scope step from it. */
describe('priorScope', () => {
  it('all rows under one registry region and area: their entry ids', () => {
    expect(priorScope([row(11, 21), row(11, 21)], scopeFields())).toEqual({ region: '11', area: '21' });
  });

  it('a region without an area keeps the area empty', () => {
    expect(priorScope([row(12, null, null, '-')], scopeFields())).toEqual({ region: '12', area: '' });
  });

  it('several regions or areas, no rows, or a row without a region: no prefill', () => {
    expect(priorScope([row(11, 21), row(12, null)], scopeFields())).toBeNull();
    expect(priorScope([row(11, 21), row(11, 22)], scopeFields())).toBeNull();
    expect(priorScope([], scopeFields())).toBeNull();
    expect(priorScope([row(null, null)], scopeFields())).toBeNull();
  });

  it('a region the picker no longer offers: no prefill; an unknown area is dropped', () => {
    expect(priorScope([row(99, null)], scopeFields())).toBeNull();
    expect(priorScope([row(11, 99)], scopeFields())).toEqual({ region: '11', area: '' });
  });

  it('List and Text columns use the stored text', () => {
    expect(priorScope([row(null, null, 'Atyrau', 'Offshore complex')], scopeFields('List', 'List'))).toEqual({ region: 'Atyrau', area: 'Offshore complex' });
    expect(priorScope([row(null, null, 'Anywhere', null)], scopeFields('Text', 'Text'))).toEqual({ region: 'Anywhere', area: '' });
  });
});
