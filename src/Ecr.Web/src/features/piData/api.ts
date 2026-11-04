import { apiFetch, apiFetchResponse } from '@/api/client';
import type { components } from '@/api/schema';
import { fileNameOf } from '@/features/audit/fileName';

/*
 * D-463 «Дані з PI» і «Звідки це значення» — клієнт до
 * `GET /api/v1/sources/{id}/raw-data/*` (Integration.View | Integration.Manage) і
 * `GET /api/v1/documents/{id}/tables/{instanceId}/cell-source*` (Document.View, рівно доступ до комірки).
 *
 * ⚠ Лише вже зібране (`ext.RawDataPoint`): у PI наживо жоден запит тут не ходить.
 * ⚠ `decimal` сервер віддає РЯДКОМ (точність понад double); на графік числа йдуть через `Number()`,
 * у таблицю — як є.
 */
export type RawDataTagsView = components['schemas']['RawDataTagsView'];
export type RawDataTag = components['schemas']['RawDataTag'];
export type RawPointView = components['schemas']['RawPointView'];
export type RawPointPage = components['schemas']['PagedResultOfRawPointView'];
export type RawSeriesView = components['schemas']['RawSeriesView'];
export type RawSeriesBucket = components['schemas']['RawSeriesBucket'];
export type RawCoverageView = components['schemas']['RawCoverageView'];
export type RawInterval = components['schemas']['RawInterval'];
export type RawCollectionStatus = components['schemas']['RawCollectionStatus'];
export type CellSourceView = components['schemas']['CellSourceView'];
export type CellSourceItem = components['schemas']['CellSourceItem'];
export type CellSourceEvent = components['schemas']['CellSourceEvent'];
export type CellFoldView = components['schemas']['CellFoldView'];
type CoverageEventPage = components['schemas']['PagedResultOfCoverageEventView'];

/** Розмір сторінки таблиці точок. */
export const PointsPageSize = 100;

/** Кошиків графіка: ширина ~1000 px дає ≤ 2 px на кошик — густіше око не розрізняє. */
export const ChartBuckets = 500;

/** Вікно запиту в UTC (ISO). */
export interface RawWindow {
  readonly fromUtc: string;
  readonly toUtc: string;
}

/** Ряд адміністративного перегляду. */
export interface RawSeriesRef extends RawWindow {
  readonly entityId: number;
  readonly path: string;
}

/** Комірка документа. */
export interface CellRef {
  readonly documentId: number;
  readonly tableInstanceId: number;
  readonly rowKey: string;
  readonly columnDefId: number;
}

function seriesQuery(ref: RawSeriesRef, extra: Record<string, string | null> = {}): string {
  const query = new URLSearchParams({ path: ref.path, fromUtc: ref.fromUtc, toUtc: ref.toUtc });
  for (const [key, value] of Object.entries(extra)) {
    if (value !== null && value.length > 0) query.set(key, value);
  }

  return query.toString();
}

/** Сутність і її теги. */
export function fetchRawTags(entityId: number): Promise<RawDataTagsView> {
  return apiFetch<RawDataTagsView>(`/api/v1/sources/${String(entityId)}/raw-data/tags`);
}

/** Сторінка точок тега, новіші першими. */
export function fetchRawPoints(ref: RawSeriesRef, cursor: string | null): Promise<RawPointPage> {
  const query = seriesQuery(ref, { cursor, limit: String(PointsPageSize) });

  return apiFetch<RawPointPage>(`/api/v1/sources/${String(ref.entityId)}/raw-data/points?${query}`);
}

/** Згорнутий ряд для графіка. */
export function fetchRawSeries(ref: RawSeriesRef): Promise<RawSeriesView> {
  const query = seriesQuery(ref, { buckets: String(ChartBuckets) });

  return apiFetch<RawSeriesView>(`/api/v1/sources/${String(ref.entityId)}/raw-data/series?${query}`);
}

/** Покриття вікна збором і стан останнього прогону. */
export function fetchRawCoverage(entityId: number, window: RawWindow): Promise<RawCoverageView> {
  const query = new URLSearchParams({ fromUtc: window.fromUtc, toUtc: window.toUtc });

  return apiFetch<RawCoverageView>(`/api/v1/sources/${String(entityId)}/raw-data/coverage?${query.toString()}`);
}

/** Події журналу покриття сутності (чому зібране не лягло в документи), новіші першими. */
export function fetchEntityCoverageEvents(
  entityId: number,
  cursor: string | null,
  periodKey: number | null = null,
): Promise<CoverageEventPage> {
  const query = new URLSearchParams({ entity: String(entityId), limit: '50' });
  if (cursor !== null) query.set('cursor', cursor);
  if (periodKey !== null) query.set('periodKey', String(periodKey));

  return apiFetch<CoverageEventPage>(`/api/v1/collection-runs/coverage-events?${query.toString()}`);
}

/** Завантажений CSV. */
export interface RawExportFile {
  readonly blob: Blob;
  readonly fileName: string | null;
}

/**
 * CSV точок тега.
 *
 * ⛔ `fetch` → blob, а не посилання: відмову (`422` понад стелю, `403`) браузер за посиланням показав би сирим JSON.
 * ⚠ `method: 'GET'` явно — сторож `EndpointCoverageTests` виводить метод із назви функції.
 */
export async function fetchRawExport(ref: RawSeriesRef): Promise<RawExportFile> {
  const query = seriesQuery(ref);
  const response = await apiFetchResponse(`/api/v1/sources/${String(ref.entityId)}/raw-data/export.csv?${query}`, {
    method: 'GET',
  });

  return { blob: await response.blob(), fileName: fileNameOf(response.headers.get('Content-Disposition')) };
}

function cellQuery(cell: CellRef, extra: Record<string, string | null> = {}): string {
  const query = new URLSearchParams({ rowKey: cell.rowKey, columnDefId: String(cell.columnDefId) });
  for (const [key, value] of Object.entries(extra)) {
    if (value !== null && value.length > 0) query.set(key, value);
  }

  return query.toString();
}

/** Звідки комірка отримує значення з PI. */
export function fetchCellSource(cell: CellRef): Promise<CellSourceView> {
  const { documentId, tableInstanceId } = cell;

  return apiFetch<CellSourceView>(
    `/api/v1/documents/${String(documentId)}/tables/${String(tableInstanceId)}/cell-source?${cellQuery(cell)}`,
  );
}

/** Сирі точки тега мапінгу за період комірки. */
export function fetchCellPoints(cell: CellRef, mapId: number, cursor: string | null): Promise<RawPointPage> {
  const { documentId, tableInstanceId } = cell;
  const query = cellQuery(cell, { mapId: String(mapId), cursor, limit: String(PointsPageSize) });

  return apiFetch<RawPointPage>(
    `/api/v1/documents/${String(documentId)}/tables/${String(tableInstanceId)}/cell-source/points?${query}`,
  );
}

/** Згорнутий ряд тега мапінгу за період комірки. */
export function fetchCellSeries(cell: CellRef, mapId: number): Promise<RawSeriesView> {
  const { documentId, tableInstanceId } = cell;
  const query = cellQuery(cell, { mapId: String(mapId), buckets: String(ChartBuckets) });

  return apiFetch<RawSeriesView>(
    `/api/v1/documents/${String(documentId)}/tables/${String(tableInstanceId)}/cell-source/series?${query}`,
  );
}
