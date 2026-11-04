import { describe, expect, it } from 'vitest';
import { entriesOfLevel, lookupLevelOf, sharedLookupLevel } from '../lookupLevel';

/** HSE401: фільтр Lookup-колонки `level=<КОД РІВНЯ>` - розбір і звуження переліку. */
describe('lookupLevelOf', () => {
  it.each([
    ['level=SITE', 'SITE'],
    [' level=PLANT_1 ', 'PLANT_1'],
    [null, null],
    [undefined, null],
    ['', null],
    ['kind=gas', null],
    ['level=', null],
    ['level=A B', null],
    ['SITE', null],
  ])('%s -> %s', (filter, expected) => {
    expect(lookupLevelOf(filter)).toBe(expected);
  });
});

describe('sharedLookupLevel', () => {
  const column = (registry: number, filter: string | null) => ({
    dataType: 'Lookup',
    lookupRegistryDefId: registry,
    lookupFilter: filter,
  });

  it('усі Lookup-колонки довідника з одним рівнем -> цей рівень', () => {
    expect(sharedLookupLevel([column(1, 'level=SITE'), column(1, 'level=site'), column(2, null)], 1)).toBe('SITE');
  });

  it('різні рівні, колонка без фільтра або немає колонок -> null (перелік спільний)', () => {
    expect(sharedLookupLevel([column(1, 'level=SITE'), column(1, 'level=AREA')], 1)).toBeNull();
    expect(sharedLookupLevel([column(1, 'level=SITE'), column(1, null)], 1)).toBeNull();
    expect(sharedLookupLevel([column(2, 'level=SITE')], 1)).toBeNull();
  });
});

describe('entriesOfLevel', () => {
  const entries = [
    { id: 1, levelCode: 'AREA' },
    { id: 2, levelCode: 'SITE' },
    { id: 3, levelCode: null },
    { id: 4 },
  ];

  it('без фільтра - усі записи', () => {
    expect(entriesOfLevel(entries, null)).toBe(entries);
  });

  it('level=SITE - лише записи цього рівня без урахування регістру', () => {
    expect(entriesOfLevel(entries, 'level=site').map((e) => e.id)).toEqual([2]);
  });
});