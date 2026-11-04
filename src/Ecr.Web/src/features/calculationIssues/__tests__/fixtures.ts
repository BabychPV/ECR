import type { DocumentTableDto } from '@/api/types';
import type { CalculationIssue } from '../api';

/** Таблиця активного аркуша для тестів проблем розрахунку (AN-69). */
export function table(ordinal: number, name: string, sheetCopyId: number | null = null): DocumentTableDto {
  return {
    allowsDynamicRows: true,
    copyOrdinal: 1,
    isCopyable: false,
    maxDynamicRows: null,
    sheetCode: 'S1',
    sheetCopyId,
    sheetDefId: 1,
    sheetNameL10n: { values: { en: 'Sheet' } },
    sheetOrdinal: 1,
    tableCode: `T${String(ordinal)}`,
    tableDefId: ordinal,
    tableInstanceId: 100 + ordinal,
    tableNameL10n: { values: { en: name } },
    tableOrdinal: ordinal,
  };
}

/** Проблема `NoMatchingRule` у таблиці `T<ordinal>`. */
export function issue(ordinal: number, rowNumber: number, over: Partial<CalculationIssue> = {}): CalculationIssue {
  return {
    detectedAt: '2026-11-04T10:00:00Z',
    kind: 'NoMatchingRule',
    methodologyCode: 'M1',
    rowKey: `row-${String(ordinal)}-${String(rowNumber)}`,
    rowNumber,
    sheetCopyId: null,
    tableCode: `T${String(ordinal)}`,
    ...over,
  };
}
