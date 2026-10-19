import type { PermitType, PermitTypeColumn, PermitTypeTable } from '@/features/permits/api/types';

/** Фікстури визначення типу для тестів діалогів і вкладок конструктора (PM-08c). */

export function column(overrides: Partial<PermitTypeColumn> = {}): PermitTypeColumn {
  return {
    code: 'Name',
    nameL10n: { en: 'Name' },
    dataType: 'String',
    unitId: null,
    required: true,
    key: true,
    group: null,
    precision: null,
    scale: null,
    listScope: null,
    listRegistryCode: null,
    siblingTableCode: null,
    choices: null,
    order: 1,
    ...overrides,
  };
}

export function table(overrides: Partial<PermitTypeTable> = {}): PermitTypeTable {
  return {
    code: 'Limits',
    nameL10n: { en: 'Limits' },
    order: 1,
    columns: [
      column(),
      column({ code: 'Annual', nameL10n: { en: 'Annual' }, dataType: 'Decimal', unitId: 3, required: false, key: false, order: 2 }),
    ],
    rules: [],
    ...overrides,
  };
}

export function permitType(overrides: Partial<PermitType> = {}): PermitType {
  return {
    code: 'WaterDischarge',
    nameL10n: { en: 'Water discharge', ru: 'Сброс воды' },
    category: 'Water',
    status: 'Draft',
    version: 1,
    draftVersion: 1,
    hasUnpublishedChanges: true,
    rowVersion: 'AAAAAAAAB9E=',
    headerFields: [],
    tables: [table()],
    rules: [],
    publishedAt: null,
    updatedAt: '2026-10-18T09:30:00Z',
    ...overrides,
  };
}

export const units = [
  { id: 3, code: 'thousand_m3', label: 'thous. m3' },
  { id: 4, code: 't', label: 't' },
] as const;
