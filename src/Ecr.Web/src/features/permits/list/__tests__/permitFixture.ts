import type { PermitListItem } from '../../api/types';

/** Рядок переліку дозволів для тестів PM-07; поля, що не названі, мають безпечні значення. */
export function makePermit(id: number, patch: Partial<PermitListItem> = {}): PermitListItem {
  return {
    id,
    number: `KZ-${String(id)}`,
    typeCode: 'AirEmission',
    typeNameL10n: { en: 'Air emission permit', ru: 'Разрешение на выбросы' },
    location: 'Central processing facility',
    status: 'Active',
    issueDate: '2026-01-01',
    validFrom: '2026-01-01',
    validTo: '2027-01-01',
    actualEnd: null,
    endsInDays: 300,
    rowCount: 10,
    tableCount: 2,
    boundSheetCount: 1,
    updatedAt: '2026-10-01T08:00:00Z',
    updatedByDisplayName: 'A. Operator',
    ...patch,
  };
}

/**
 * Чотири дозволи з макета: чинний, що скоро закінчується; чинний без читачів; чернетка; закінчений.
 * Два типи: повітряний і водний.
 */
export const FixturePermits: readonly PermitListItem[] = [
  makePermit(1, { number: 'KZ06VCZ14825472', endsInDays: 40, boundSheetCount: 3, rowCount: 43 }),
  makePermit(2, {
    number: 'KZ14VCZ14523270',
    typeCode: 'WaterDischarge',
    typeNameL10n: { en: 'Water discharge permit' },
    location: 'Offshore complex',
    tableCount: 1,
    boundSheetCount: 0,
    endsInDays: 200,
  }),
  makePermit(3, { number: 'DRAFT-2027', status: 'Draft', validFrom: '2027-01-01', validTo: '2028-01-01', endsInDays: null, boundSheetCount: 0 }),
  makePermit(4, {
    number: 'OLD-2024',
    status: 'Expired',
    validFrom: '2024-01-01',
    validTo: '2025-01-01',
    actualEnd: '2024-09-01',
    endsInDays: null,
    boundSheetCount: 2,
  }),
];
