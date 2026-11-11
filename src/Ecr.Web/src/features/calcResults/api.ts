import { apiEnqueue, apiFetch, type AcceptedJob } from '@/api/client';
import type { components } from '@/api/schema';

/** Зведення «Calculation results» за рік (`GET /documents/{id}/calculation-results/year/summary`). */
export type CalculationYearSummary = components['schemas']['CalculationYearSummaryDto'];
export type CalculationYearTable = components['schemas']['CalculationYearTableSummaryDto'];
export type CalculationYearMonth = components['schemas']['CalculationYearMonthDto'];
/** Сторінка виходів таблиці (`GET …/year/rows`): індекси рядків — у переліки ЦІЄЇ сторінки. */
export type CalculationYearRows = components['schemas']['CalculationYearRowsDto'];
export type CalculationYearRow = components['schemas']['CalculationYearRowDto'];
export type CalculationYearEquipment = components['schemas']['CalculationYearEquipmentDto'];
export type CalculationYearMethodology = components['schemas']['CalculationYearMethodologyDto'];
export type CalculationYearSubstance = components['schemas']['CalculationYearSubstanceDto'];
export type CalculationYearUnit = components['schemas']['CalculationYearUnitDto'];
export type CalculationYearColumn = components['schemas']['CalculationYearColumnDto'];
export type CalculationYearExportRequest = components['schemas']['CalculationYearExportRequest'];

/** Розкладка книги експорту. */
export type ExportLayout = 'pivot' | 'long';

/** Розмір сторінки рядків таблиці. */
export const PageSize = 200;

/** Фільтри екрана — ті самі, що й на сервері (`CalculationYearFilter`). */
export interface ResultFilters {
  readonly query: string;
  /** `sheetDefId` (рядком) або порожньо. */
  readonly sheet: string;
  /** Код методології; `formula` — формули шаблону; порожньо — усі. */
  readonly methodology: string;
  /** Код речовини або порожньо. */
  readonly substance: string;
  readonly problemsOnly: boolean;
}

export const NoFilters: ResultFilters = { query: '', sheet: '', methodology: '', substance: '', problemsOnly: false };

/** Код «методології» для формул шаблону у фільтрі. */
export const FormulaMethodology = 'formula';

/** Чи фільтри щось відсікають. */
export function isFiltered(filters: ResultFilters): boolean {
  return filters.query.trim() !== '' || filters.sheet !== '' || filters.methodology !== '' || filters.substance !== '' || filters.problemsOnly;
}

/** Параметри запиту з фільтрів (порожні не передаються — той самий ключ кешу, та сама адреса). */
export function filterParams(filters: ResultFilters): URLSearchParams {
  const params = new URLSearchParams();
  if (filters.query.trim() !== '') params.set('q', filters.query.trim());
  if (filters.sheet !== '') params.set('sheet', filters.sheet);
  if (filters.methodology !== '') params.set('methodology', filters.methodology);
  if (filters.substance !== '') params.set('substance', filters.substance);
  if (filters.problemsOnly) params.set('problems', 'true');
  return params;
}

// ⚠ Адреси — повними літералами (сторож EndpointCoverageTests шукає шлях від `/api/v1` до кінця рядка шаблону).

/** Зведення: місяці, таблиці з лічильниками, варіанти фільтрів — без чисел (кілобайти навіть для Land). */
export function fetchCalculationYearSummary(
  documentId: number,
  year: number,
  filters: ResultFilters,
): Promise<CalculationYearSummary> {
  const params = filterParams(filters);
  params.set('year', String(year));
  return apiFetch<CalculationYearSummary>(
    `/api/v1/documents/${String(documentId)}/calculation-results/year/summary?${params.toString()}`,
  );
}

/** Сторінка виходів таблиці `table` (ключ зі зведення). */
export function fetchCalculationYearRows(
  documentId: number,
  year: number,
  table: string,
  filters: ResultFilters,
  offset: number,
): Promise<CalculationYearRows> {
  const params = filterParams(filters);
  params.set('year', String(year));
  params.set('table', table);
  params.set('offset', String(offset));
  params.set('limit', String(PageSize));
  return apiFetch<CalculationYearRows>(
    `/api/v1/documents/${String(documentId)}/calculation-results/year/rows?${params.toString()}`,
  );
}

/**
 * Ставить книгу огляду в чергу; файл забирається тим самим `GET …/export/{exportId}`, що й книга документа.
 * «Як відфільтровано» — ті самі фільтри, що й на екрані (сервер застосовує їх до всього документа).
 */
export function enqueueCalculationYearExport(
  documentId: number,
  year: number,
  layout: ExportLayout,
  language: string,
  filters: ResultFilters | null,
): Promise<AcceptedJob> {
  const sheet = filters === null || filters.sheet === '' ? null : Number(filters.sheet);
  const request: CalculationYearExportRequest = {
    year,
    layout,
    language,
    filtered: filters !== null,
    query: filters === null || filters.query.trim() === '' ? null : filters.query.trim(),
    sheet,
    methodology: filters === null || filters.methodology === '' ? null : filters.methodology,
    substance: filters === null || filters.substance === '' ? null : filters.substance,
    problemsOnly: filters?.problemsOnly ?? false,
  };
  return apiEnqueue(`/api/v1/documents/${String(documentId)}/calculation-results/year/export`, request);
}

/** Адреса готової книги (той самий маршрут, що й експорт документа). */
export function exportDownloadHref(documentId: number, exportId: string): string {
  return `/api/v1/documents/${String(documentId)}/export/${encodeURIComponent(exportId)}`;
}
