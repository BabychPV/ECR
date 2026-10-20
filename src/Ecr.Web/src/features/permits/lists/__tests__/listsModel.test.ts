import { describe, expect, it } from 'vitest';
import type { PermitListDto, PermitListUseDto } from '../listsApi';
import { filterLists, kindOfScope, kindParam, listKeyOf, listsStats, rowsFromLists, valueLabels } from '../listsModel';

/**
 * Модель екрана «Lists» (PM-08d, PM-08e). Мутаційний доказ (вручну, відновлено): без `if (kind === null) continue` невідома
 * область дала б рядок без походження (падає «невідома область пропущена»); `KindOrder` без спільних першими валить
 * «спершу спільні»; `valueLabels` без `existing?.en` перезаписав би англійську назву значення «лише для типу» при
 * перейменуванні російською (падає «перейменування іншою мовою лишає англійську»).
 */
const use = (type: string, table: string, field: string): PermitListUseDto => ({
  typeCode: type,
  typeNameL10n: { en: `${type} permit` },
  tableCode: table,
  tableNameL10n: { en: table },
  fieldCode: field,
  fieldNameL10n: { en: field },
});

const base = { typeCode: null, registryCode: null, itemCount: null, editable: false, usedBy: [] } satisfies Partial<PermitListDto>;

const lists: PermitListDto[] = [
  {
    ...base,
    key: 'type:Air:Emission:Method',
    scope: 'TypeOnly',
    registryCode: 'PRM_Air_Emission_Method',
    typeCode: 'Air',
    nameL10n: { en: 'Method' },
    itemCount: 2,
    usedBy: [use('Air', 'Emission', 'Method')],
  },
  {
    ...base,
    key: 'permit:Air:Sources:Kind',
    scope: 'SamePermitTable',
    typeCode: 'Air',
    nameL10n: { en: 'Kind' },
    usedBy: [use('Air', 'Sources', 'Kind')],
  },
  {
    ...base,
    key: 'shared:SUBSTANCES',
    scope: 'SharedRegistry',
    registryCode: 'SUBSTANCES',
    nameL10n: { en: 'Substances' },
    itemCount: 42,
    editable: true,
    usedBy: [use('Air', 'Emission', 'Substance'), use('Water', 'Discharge', 'Substance')],
  },
  { ...base, key: 'shared:REGIONS', scope: 'SharedRegistry', registryCode: 'REGIONS', nameL10n: {}, usedBy: [use('Air', 'Emission', 'Region')] },
  { ...base, key: 'strange:1', scope: 'Unknown', nameL10n: { en: 'Strange' } },
];

describe('rowsFromLists', () => {
  const rows = rowsFromLists(lists, 'en');

  it('спершу спільні, далі «лише тип» і «таблиця дозволу»; невідома область пропущена', () => {
    expect(rows.map((row) => row.id)).toEqual([
      'shared:REGIONS',
      'shared:SUBSTANCES',
      'type:Air:Emission:Method',
      'permit:Air:Sources:Kind',
    ]);
  });

  it('спільний: усі згадки в «used», кількість і право зі списку сервера, власника немає', () => {
    const substances = rows.find((row) => row.registryCode === 'SUBSTANCES');

    expect(substances).toMatchObject({ name: 'Substances', owner: '', entries: 42, editable: true, kind: 'shared' });
    expect(substances?.used.map((item) => `${item.typeCode}.${item.tableCode}.${item.columnCode}`)).toEqual([
      'Air.Emission.Substance',
      'Water.Discharge.Substance',
    ]);
  });

  it('без назви — код довідника; «лише тип»: власник — назва типу, значень на клієнті немає', () => {
    expect(rows.find((row) => row.id === 'shared:REGIONS')?.name).toBe('REGIONS');
    expect(rows.find((row) => row.kind === 'type')).toMatchObject({
      name: 'Method',
      owner: 'Air permit',
      typeCode: 'Air',
      entries: 2,
      editable: false,
    });
  });

  it('таблиця того ж дозволу: кількість значень невідома', () => {
    expect(rows.find((row) => row.kind === 'permit')).toMatchObject({ name: 'Kind', entries: null });
  });

  it('ідентифікатори — ключі сервера, унікальні', () => {
    expect(new Set(rows.map((row) => row.id)).size).toBe(rows.length);
    expect(kindOfScope('SharedRegistry')).toBe('shared');
    expect(kindOfScope('Unknown')).toBeNull();
  });
});

describe('listsStats / filterLists', () => {
  const rows = rowsFromLists(lists, 'en');

  it('лічильники за походженням', () => {
    expect(listsStats(rows)).toEqual({ all: 4, shared: 2, type: 1, permit: 1 });
  });

  it('пошук за назвою, власником і кодом довідника', () => {
    expect(filterLists(rows, { needle: 'subst', kind: '' })).toHaveLength(1);
    expect(filterLists(rows, { needle: 'AIR PERMIT', kind: '' }).map((row) => row.name)).toEqual(['Method', 'Kind']);
    expect(filterLists(rows, { needle: 'regions', kind: '' })).toHaveLength(1);
  });

  it('походження звужує, чужа назва походження — ні', () => {
    expect(filterLists(rows, { needle: '', kind: 'type' })).toHaveLength(1);
    expect(kindParam('bogus')).toBe('');
    expect(kindParam('permit')).toBe('permit');
    expect(kindParam(null)).toBe('');
  });
});

describe('valueLabels', () => {
  it('англійська: лише en', () => {
    expect(valueLabels('  Cooling ', undefined, 'en')).toEqual({ en: 'Cooling' });
  });

  it('нове значення іншою мовою: en дорівнює введеному, плюс власна мова', () => {
    expect(valueLabels('Охлаждение', undefined, 'ru')).toEqual({ en: 'Охлаждение', ru: 'Охлаждение' });
  });

  it('перейменування іншою мовою лишає англійську як була', () => {
    expect(valueLabels('Охлаждение 2', { en: 'Cooling', ru: 'Охлаждение' }, 'ru')).toEqual({ en: 'Cooling', ru: 'Охлаждение 2' });
  });
});

describe('listKeyOf', () => {
  it('спільний — за довідником, лише тип — за стовпцем, таблиця дозволу й невідоме — без ключа', () => {
    expect(listKeyOf('SharedRegistry', 'WD', 'Limits', 'Substance', 'Substances')).toBe('shared:Substances');
    expect(listKeyOf('TypeOnly', 'WD', 'Limits', 'Use', 'PRM_WD_Limits_Use')).toBe('type:WD:Limits:Use');
    expect(listKeyOf('SamePermitTable', 'WD', 'Limits', 'Place', null)).toBeNull();
    expect(listKeyOf('SharedRegistry', 'WD', 'Limits', 'Substance', null)).toBeNull();
    expect(listKeyOf(null, 'WD', 'Limits', 'Name', null)).toBeNull();
  });
});
