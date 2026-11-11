import { apiEnqueue, apiFetch, type AcceptedJob } from '@/api/client';
import type { components } from '@/api/schema';

/** Огляд «Calculation results» за рік (`GET /documents/{id}/calculation-results/year`). */
export type CalculationYearView = components['schemas']['CalculationYearViewDto'];
export type CalculationYearRow = components['schemas']['CalculationYearRowDto'];
export type CalculationYearMonth = components['schemas']['CalculationYearMonthDto'];
export type CalculationYearExportRequest = components['schemas']['CalculationYearExportRequest'];

/** Розкладка книги експорту. */
export type ExportLayout = 'pivot' | 'long';

/** Один запит на документ × рік (CALC-RESULTS.md §3). */
export function fetchCalculationYear(documentId: number, year: number): Promise<CalculationYearView> {
  return apiFetch<CalculationYearView>(
    `/api/v1/documents/${String(documentId)}/calculation-results/year?year=${String(year)}`,
  );
}

/**
 * Ставить книгу огляду в чергу; файл забирається тим самим `GET …/export/{exportId}`, що й книга документа.
 * `rowIds` — виходи «як відфільтровано»; `undefined` — увесь документ.
 */
export function enqueueCalculationYearExport(
  documentId: number,
  request: CalculationYearExportRequest,
): Promise<AcceptedJob> {
  return apiEnqueue(
    `/api/v1/documents/${String(documentId)}/calculation-results/year/export`,
    request satisfies CalculationYearExportRequest,
  );
}

/** Адреса готової книги (той самий маршрут, що й експорт документа). */
export function exportDownloadHref(documentId: number, exportId: string): string {
  return `/api/v1/documents/${String(documentId)}/export/${encodeURIComponent(exportId)}`;
}
