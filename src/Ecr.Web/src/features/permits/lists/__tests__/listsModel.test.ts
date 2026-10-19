import { describe, expect, it } from 'vitest';
import type { RegistryDefDto } from '@/api/types';
import { column, permitType, table } from '../../typeEditor/__tests__/fixtures';
import { buildListRows, filterLists, kindParam, listsStats } from '../listsModel';

/**
 * Модель екрана «Lists» (PM-08d). Мутаційний доказ (вручну, відновлено): без `continue` після `shared` рядок
 * довідника дублювався б на кожен стовпець (падає «один рядок на довідник»); без `known.used.push` друга
 * згадка довідника губилась би (падає лічильник «використовується»); `scopeKeyOf` без перевірки `Lookup`
 * вважав би списком звичайний стовпець (падає «не-списки пропущені»).
 */
const registries = [
  { code: 'SUBSTANCES', nameL10n: { values: { en: 'Substances' } }, entryCount: 42 },
  { code: 'UNUSED', nameL10n: { values: { en: 'Not used' } }, entryCount: 3 },
] as unknown as RegistryDefDto[];

const sharedCol = (code: string, registry: string) =>
  column(code, { dataType: 'Lookup', listScope: 'SharedRegistry', listRegistryCode: registry });

const air = permitType({
  code: 'Air',
  nameL10n: { en: 'Air permit' },
  headerFields: [sharedCol('Region', 'REGIONS')],
  tables: [
    table('Emission', [
      sharedCol('Substance', 'SUBSTANCES'),
      column('Method', { dataType: 'Lookup', listScope: 'TypeOnly', choices: ['Direct', 'Calc'] }),
      column('Annual', { dataType: 'Decimal' }),
    ]),
    table('Sources', [column('Name', { key: true }), column('Kind', { dataType: 'Lookup', listScope: 'SamePermitTable', siblingTableCode: 'Emission' })]),
  ],
});
const water = permitType({
  code: 'Water',
  nameL10n: { en: 'Water permit' },
  headerFields: [],
  tables: [table('Discharge', [sharedCol('Substance', 'SUBSTANCES')])],
});

describe('buildListRows', () => {
  const rows = buildListRows([air, water], registries, 'en');

  it('один рядок на довідник, усі згадки — у «used»', () => {
    const shared = rows.filter((row) => row.kind === 'shared');
    expect(shared.map((row) => row.registryCode)).toEqual(['REGIONS', 'SUBSTANCES']);
    const substances = shared.find((row) => row.registryCode === 'SUBSTANCES');
    expect(substances?.used.map((use) => `${use.typeCode}.${use.tableCode}.${use.columnCode}`)).toEqual([
      'Air.Emission.Substance',
      'Water.Discharge.Substance',
    ]);
    expect(substances?.name).toBe('Substances');
    expect(substances?.entries).toBe(42);
  });

  it('довідник без запису в реєстрах: назва — код, кількість невідома; поле шапки без таблиці', () => {
    const regions = rows.find((row) => row.registryCode === 'REGIONS');
    expect(regions?.name).toBe('REGIONS');
    expect(regions?.entries).toBeNull();
    expect(regions?.used[0]?.tableCode).toBeNull();
  });

  it('«лише тип»: по рядку на стовпець, значення читаються, власник — тип', () => {
    const own = rows.find((row) => row.kind === 'type');
    expect(own).toMatchObject({ name: 'Method', owner: 'Air permit', typeCode: 'Air', entries: 2, choices: ['Direct', 'Calc'] });
  });

  it('таблиця того ж дозволу: кількість значень невідома', () => {
    const own = rows.find((row) => row.kind === 'permit');
    expect(own).toMatchObject({ name: 'Kind', entries: null, choices: [] });
  });

  it('не-списки й невикористані довідники пропущені; спершу спільні', () => {
    expect(rows).toHaveLength(4);
    expect(rows.some((row) => row.registryCode === 'UNUSED')).toBe(false);
    expect(rows.map((row) => row.kind)).toEqual(['shared', 'shared', 'type', 'permit']);
  });

  it('ідентифікатори унікальні', () => {
    expect(new Set(rows.map((row) => row.id)).size).toBe(rows.length);
  });
});

describe('listsStats / filterLists', () => {
  const rows = buildListRows([air, water], registries, 'en');

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
