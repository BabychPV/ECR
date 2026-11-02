import type { PermitUsage, PermitUsageItem } from '../../api/usagesApi';

/** Місце-шапка: поле `PERMIT` документа 2/202606 проєкту LAND. */
export function headerItem(over: Partial<PermitUsageItem> = {}): PermitUsageItem {
  return {
    kind: 'header',
    documentId: 2,
    documentKey: 'LAND-2026-06',
    projectId: 1,
    projectCode: 'LAND',
    periodKey: null,
    sheetCode: null,
    sheetNameL10n: null,
    tableCode: null,
    tableNameL10n: null,
    rowKey: null,
    rowOrdinal: null,
    fieldCode: 'PERMIT',
    fieldLabelL10n: { en: 'Permit number', ru: 'Номер разрешения' },
    ...over,
  };
}

/** Місце-комірка: колонка `PCOL` рядка 3 таблиці «Water use» аркуша «Water». */
export function cellItem(over: Partial<PermitUsageItem> = {}): PermitUsageItem {
  return {
    kind: 'cell',
    documentId: 2,
    documentKey: 'LAND-2026-06',
    projectId: 1,
    projectCode: 'LAND',
    periodKey: 202606,
    sheetCode: 'WATER',
    sheetNameL10n: { en: 'Water' },
    tableCode: 'USE',
    tableNameL10n: { en: 'Water use' },
    rowKey: 'R3',
    rowOrdinal: 3,
    fieldCode: 'PCOL',
    fieldLabelL10n: { en: 'Permit' },
    ...over,
  };
}

export function usage(items: readonly PermitUsageItem[], over: Partial<PermitUsage> = {}): PermitUsage {
  return {
    permitId: 42,
    documentCount: new Set(items.map((item) => item.documentId)).size,
    linkCount: items.length,
    truncated: false,
    items: [...items],
    ...over,
  };
}
