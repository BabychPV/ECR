import { describe, expect, it, vi } from 'vitest';
import {
  EndingSoonDays,
  actionsFor,
  asPermitStat,
  filterPermits,
  isEndingSoon,
  isNotRead,
  listStats,
  matchesNeedle,
  permitHref,
  permitTypeName,
  readByText,
  rowsText,
  typeOptions,
} from '../permitsListModel';
import { FixturePermits, makePermit } from './permitFixture';

vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    params === undefined ? key : `${key} ${JSON.stringify(params)}`,
  language: () => 'en',
}));

/**
 * Модель переліку дозволів (PM-07). Мутаційний доказ (вручну, відновлено): `<=` на `<` у `isEndingSoon` валить
 * «межа 120 днів включна»; `actionsFor('Expired')` з `'end'` валить «закінчений лише поновлюють»; без
 * `status !== 'Expired'` в `isNotRead` закінчений без аркушів потрапляє в «не читає жоден аркуш».
 */
describe('listStats', () => {
  it('рахує всі, чинні, ті що скоро закінчуються, і ті що ніхто не читає', () => {
    expect(listStats(FixturePermits)).toEqual({ all: 4, active: 2, ending: 1, free: 2 });
  });

  it('порожній перелік — нулі', () => {
    expect(listStats([])).toEqual({ all: 0, active: 0, ending: 0, free: 0 });
  });
});

describe('isEndingSoon', () => {
  it(`межа ${String(EndingSoonDays)} днів включна, 121 — уже ні`, () => {
    expect(isEndingSoon(makePermit(1, { endsInDays: EndingSoonDays }))).toBe(true);
    expect(isEndingSoon(makePermit(1, { endsInDays: EndingSoonDays + 1 }))).toBe(false);
  });

  it('лише чинний, і лише коли сервер дав кількість днів', () => {
    expect(isEndingSoon(makePermit(1, { endsInDays: null }))).toBe(false);
    expect(isEndingSoon(makePermit(1, { status: 'Draft', endsInDays: 5 }))).toBe(false);
  });
});

describe('isNotRead', () => {
  it('закінчений без читачів у показник не входить, чернетка без читачів — входить', () => {
    expect(isNotRead(makePermit(1, { status: 'Expired', boundSheetCount: 0 }))).toBe(false);
    expect(isNotRead(makePermit(1, { status: 'Draft', boundSheetCount: 0 }))).toBe(true);
    expect(isNotRead(makePermit(1, { boundSheetCount: 2 }))).toBe(false);
  });
});

describe('filterPermits', () => {
  const none = { needle: '', type: '', state: '', stat: null } as const;

  it('без фільтрів віддає все', () => {
    expect(filterPermits(FixturePermits, none)).toHaveLength(4);
  });

  it('тип і стан перетинаються (І)', () => {
    expect(filterPermits(FixturePermits, { ...none, type: 'WaterDischarge' }).map((p) => p.id)).toEqual([2]);
    expect(filterPermits(FixturePermits, { ...none, state: 'Draft' }).map((p) => p.id)).toEqual([3]);
    expect(filterPermits(FixturePermits, { ...none, type: 'WaterDischarge', state: 'Draft' })).toEqual([]);
  });

  it('показники: active, ending, free', () => {
    expect(filterPermits(FixturePermits, { ...none, stat: 'active' }).map((p) => p.id)).toEqual([1, 2]);
    expect(filterPermits(FixturePermits, { ...none, stat: 'ending' }).map((p) => p.id)).toEqual([1]);
    expect(filterPermits(FixturePermits, { ...none, stat: 'free' }).map((p) => p.id)).toEqual([2, 3]);
  });

  it('пошук: номер, розташування, назва типу будь-якою мовою, регістр не важить', () => {
    expect(matchesNeedle(FixturePermits[0]!, 'kz06')).toBe(true);
    expect(matchesNeedle(FixturePermits[1]!, 'OFFSHORE')).toBe(true);
    expect(matchesNeedle(FixturePermits[0]!, 'разрешение')).toBe(true);
    expect(matchesNeedle(FixturePermits[0]!, 'zzz')).toBe(false);
    expect(matchesNeedle(makePermit(9, { location: null }), '  ')).toBe(true);
  });
});

describe('typeOptions і назва типу', () => {
  it('тип лише раз, відсортовано за назвою; порожня назва замінюється кодом', () => {
    const items = [...FixturePermits, makePermit(5, { typeCode: 'Waste', typeNameL10n: {} })];
    expect(typeOptions(items, 'en')).toEqual([
      { value: 'AirEmission', label: 'Air emission permit' },
      { value: 'Waste', label: 'Waste' },
      { value: 'WaterDischarge', label: 'Water discharge permit' },
    ]);
    expect(permitTypeName(makePermit(5, { typeCode: 'Waste', typeNameL10n: {} }), 'en')).toBe('Waste');
  });
});

describe('actionsFor', () => {
  it('чернетку активують чи видаляють, чинний поновлюють чи завершують, закінчений лише поновлюють', () => {
    expect(actionsFor('Draft')).toEqual(['activate', 'delete']);
    expect(actionsFor('Active')).toEqual(['renew', 'end']);
    expect(actionsFor('Expired')).toEqual(['renew']);
  });

  it('невідомий стан не пропонує нічого', () => {
    expect(actionsFor('Archived')).toEqual([]);
  });
});

describe('тексти рядка', () => {
  it('рядки й читачі: однина окремо від множини; без читачів — null', () => {
    expect(rowsText(makePermit(1, { rowCount: 43, tableCount: 1 }))).toBe('permits.list.rowsOne {"rows":43}');
    expect(rowsText(makePermit(1, { rowCount: 5, tableCount: 3 }))).toBe('permits.list.rowsMany {"rows":5,"tables":3}');
    expect(readByText(makePermit(1, { boundSheetCount: 0 }))).toBeNull();
    expect(readByText(makePermit(1, { boundSheetCount: 1 }))).toBe('permits.list.readByOne {"count":1}');
    expect(readByText(makePermit(1, { boundSheetCount: 4 }))).toBe('permits.list.readByMany {"count":4}');
  });

  it('адреса сторінки дозволу і розбір показника з адреси', () => {
    expect(permitHref(12)).toBe('/admin/permits/12');
    expect(asPermitStat('ending')).toBe('ending');
    expect(asPermitStat('bogus')).toBeNull();
    expect(asPermitStat(null)).toBeNull();
  });
});
