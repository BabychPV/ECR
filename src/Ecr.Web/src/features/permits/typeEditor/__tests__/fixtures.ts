import type { PermitType, PermitTypeColumn, PermitTypeTable } from '../../api/types';
import { draftOf, newColumn, type TypeDraft } from '../draftModel';

/* Заготовки для тестів редактора типу (PM-08b): повний `PermitTypeDto` і його робоча копія. */

export function column(code: string, over: Partial<PermitTypeColumn> = {}): PermitTypeColumn {
  return {
    ...newColumn(code, { en: code, ru: `${code} ru` }),
    ...over,
  };
}

export function table(code: string, columns: PermitTypeColumn[], over: Partial<PermitTypeTable> = {}): PermitTypeTable {
  const ordered = columns.map((c, index) => ({ ...c, order: index + 1 }));
  return { code, nameL10n: { en: code, ru: `${code} ru` }, order: 1, columns: ordered, rules: [], ...over };
}

export const headerFields: PermitTypeColumn[] = [
  column('Permit_Number', { key: true, required: true, nameL10n: { en: 'Permit number' } }),
  column('Issue_Date', { dataType: 'Date', nameL10n: { en: 'Issue date' } }),
];

export function permitType(over: Partial<PermitType> = {}): PermitType {
  return {
    code: 'WaterDischarge',
    nameL10n: { en: 'Water discharge', ru: 'Сброс воды' },
    category: 'Water',
    status: 'Draft',
    version: 0,
    draftVersion: 1,
    hasUnpublishedChanges: true,
    rowVersion: 'rv-1',
    headerFields,
    tables: [
      table('Discharge', [
        column('Water_body', { key: true, required: true }),
        column('Sub_period', { key: true, required: true, dataType: 'Date' }),
        column('Annual', { dataType: 'Decimal', precision: 12, scale: 3, unitId: 7 }),
      ]),
      table('Storage', [column('Place', { key: true, required: true })], { order: 2 }),
    ],
    rules: [],
    publishedAt: null,
    updatedAt: '2026-10-18T10:00:00Z',
    ...over,
  } as PermitType;
}

export function draftFixture(over: Partial<PermitType> = {}): TypeDraft {
  return draftOf(permitType(over));
}
