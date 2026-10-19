import type { PermitTypeListItem } from '../../api/types';

/** Рядок переліку типів для тестів PM-08a; поля, що не названі, мають безпечні значення. */
export function makeType(code: string, patch: Partial<PermitTypeListItem> = {}): PermitTypeListItem {
  return {
    code,
    category: 'Air',
    hasUnpublishedChanges: false,
    nameL10n: { en: code },
    permitCount: 0,
    status: 'Published',
    tables: [],
    updatedAt: '2026-10-01T08:00:00Z',
    updatedByDisplayName: 'A. Operator',
    version: 1,
    ...patch,
  };
}

/** Три типи з макета: опублікований повітряний, водний зі змінами в чернетці, відходний-чернетка. */
export const FixtureTypes: readonly PermitTypeListItem[] = [
  makeType('AirEmission', {
    category: 'Air',
    nameL10n: { en: 'Air emission permit', ru: 'Разрешение на выбросы' },
    permitCount: 4,
    version: 3,
    tables: [
      { code: 'Limits', nameL10n: { en: 'Emission limits' }, columnCount: 6 },
      { code: 'Sources', nameL10n: { en: 'Sources' }, columnCount: 4 },
    ],
  }),
  makeType('WaterDischarge', {
    category: 'Water',
    nameL10n: { en: 'Water discharge permit', ru: 'Разрешение на сбросы' },
    permitCount: 2,
    version: 2,
    hasUnpublishedChanges: true,
    updatedByDisplayName: 'B. Engineer',
    tables: [{ code: 'Volumes', nameL10n: { en: 'Discharge volumes' }, columnCount: 12 }],
  }),
  makeType('WasteStorage', {
    category: 'Waste',
    nameL10n: { en: 'Waste storage permit' },
    status: 'Draft',
    hasUnpublishedChanges: true,
    version: 1,
    tables: [{ code: 'Places', nameL10n: { en: 'Storage places' }, columnCount: 5 }],
  }),
];
