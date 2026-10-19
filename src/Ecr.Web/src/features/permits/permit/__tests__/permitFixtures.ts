import type { RegistryRow } from '@/features/registries/rows/api';
import type { Permit, PermitType, PermitTypeColumn, PermitTypeTable } from '../../api/types';

/* Заготовки для тестів екрана дозволу (PM-07): дозвіл, тип із двома таблицями, рядки реєстрів. */

export function column(code: string, over: Partial<PermitTypeColumn> = {}): PermitTypeColumn {
  return {
    code,
    nameL10n: { en: code },
    dataType: 'String',
    group: null,
    key: false,
    required: false,
    order: 1,
    precision: null,
    scale: null,
    unitId: null,
    choices: null,
    listRegistryCode: null,
    listScope: null,
    siblingTableCode: null,
    ...over,
  };
}

function table(code: string, columns: PermitTypeColumn[]): PermitTypeTable {
  return {
    code,
    nameL10n: { en: code },
    order: 1,
    columns: columns.map((entry, index) => ({ ...entry, order: index + 1 })),
    rules: [],
  };
}

export const LimitsRegistry = 'PRM_WD_Limits';
export const PlacesRegistry = 'PRM_WD_Places';

export function permitType(): PermitType {
  return {
    code: 'WaterDischarge',
    nameL10n: { en: 'Water discharge' },
    category: 'Water',
    status: 'Published',
    version: 1,
    draftVersion: null,
    hasUnpublishedChanges: false,
    rowVersion: 'type-rv',
    headerFields: [],
    rules: [],
    publishedAt: null,
    updatedAt: null,
    tables: [
      table('Limits', [
        column('Name', { key: true, required: true, nameL10n: { en: 'Name' } }),
        column('Annual', { dataType: 'Decimal', required: true, unitId: 7, nameL10n: { en: 'Annual volume' } }),
        column('Count', { dataType: 'Int', nameL10n: { en: 'Count' } }),
        column('Active', { dataType: 'Bool', nameL10n: { en: 'Active' } }),
        column('Issued', { dataType: 'Date', nameL10n: { en: 'Issued on' } }),
        column('Place', {
          dataType: 'Lookup',
          listScope: 'SamePermitTable',
          siblingTableCode: 'Places',
          nameL10n: { en: 'Place' },
        }),
        column('Use', {
          dataType: 'Lookup',
          listScope: 'TypeOnly',
          listRegistryCode: 'PRM_WD_Use',
          choices: ['Cooling', 'Process'],
          nameL10n: { en: 'Type of use' },
        }),
        column('Substance', {
          dataType: 'Lookup',
          listScope: 'SharedRegistry',
          listRegistryCode: 'Substances',
          nameL10n: { en: 'Substance' },
        }),
      ]),
      table('Places', [column('Name', { key: true, required: true, nameL10n: { en: 'Place name' } })]),
    ],
  };
}

export function permit(over: Partial<Permit> = {}): Permit {
  return {
    id: 5,
    number: 'KZ-100',
    typeCode: 'WaterDischarge',
    typeNameL10n: { en: 'Water discharge' },
    typeVersion: 1,
    category: 'Water',
    status: 'Draft',
    location: 'Central facility',
    issueDate: '2026-01-05',
    validFrom: '2026-02-01',
    validTo: '2027-02-01',
    actualEnd: null,
    endsInDays: null,
    reportingFlags: { form300: true, form301: false, form302: false, tox: false },
    rowVersion: 'permit-rv-1',
    updatedAt: null,
    updatedByDisplayName: null,
    boundSheets: [],
    tables: [
      {
        code: 'Limits',
        nameL10n: { en: 'Limits' },
        registryCode: LimitsRegistry,
        rowCount: 2,
        total: '30',
        totalColumnCode: 'Annual',
      },
      { code: 'Places', nameL10n: { en: 'Places' }, registryCode: PlacesRegistry, rowCount: 1, total: null, totalColumnCode: null },
    ],
    ...over,
  } as Permit;
}

export function registryRow(id: number, values: Record<string, string | [string, string]>, over: Partial<RegistryRow> = {}): RegistryRow {
  return {
    id,
    code: `R${String(id)}`,
    display: `R${String(id)}`,
    parentEntryId: null,
    validFrom: null,
    validTo: null,
    version: `v${String(id)}`,
    values: Object.fromEntries(
      Object.entries(values).map(([code, value]) => [
        code,
        Array.isArray(value) ? { value: value[0], display: value[1], unit: null } : { value, display: null, unit: null },
      ]),
    ),
    ...over,
  } as RegistryRow;
}

export function page(items: RegistryRow[]) {
  return { items, nextCursor: null, totalCount: items.length };
}

/** Рядки реєстрів, які читає екран: таблиці дозволу, список типу, спільний довідник. */
export function rowsFor(code: string, parentEntryId?: number) {
  void parentEntryId;
  switch (code) {
    case LimitsRegistry:
      return page([
        registryRow(11, { Name: 'Outfall A', Annual: '10', Active: 'true', Issued: '2026-03-01', Place: ['31', 'Pond 1'], Use: ['21', 'Cooling'] }),
        registryRow(12, { Name: 'Outfall B', Annual: '20' }),
      ]);
    case PlacesRegistry:
      return page([registryRow(31, { Name: 'Pond 1' }, { display: 'Pond 1' })]);
    case 'PRM_WD_Use':
      return page([registryRow(21, {}, { display: 'Cooling' }), registryRow(22, {}, { display: 'Process' })]);
    case 'Substances':
      return page([registryRow(41, {}, { display: 'Methane' }), registryRow(42, {}, { display: 'Benzene' })]);
    default:
      return page([]);
  }
}
