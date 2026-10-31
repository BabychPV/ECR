import { apiFetch, apiFetchResponse } from '@/api/client';
import type { components } from '@/api/schema';
import { fileNameOf } from '@/features/audit/fileName';
import { formatDateTime } from '@/shared/format';
import type { RawExportFile, RawWindow } from './api';

/*
 * D-465 «Дані з PI» для подій PI (Event Frames) — клієнт до `GET /api/v1/sources/{id}/raw-data/events*`
 * (Integration.View | Integration.Manage).
 *
 * ⚠ Лише сховище: значення атрибутів — останнє прочитання синку, у PI наживо жоден запит не ходить.
 * ⚠ Документ, якого людина не бачить, сервер віддає без ключа рядка, документа й значень комірок.
 */
export type PiEventMapsView = components['schemas']['PiEventMapsView'];
export type PiEventMapView = components['schemas']['PiEventMapView'];
export type PiEventView = components['schemas']['PiEventView'];
export type PiEventAttributeView = components['schemas']['PiEventAttributeView'];
export type PiEventRemovalsView = components['schemas']['PiEventRemovalsView'];
export type PiEventRemovalView = components['schemas']['PiEventRemovalView'];
export type CellEventSourceItem = components['schemas']['CellEventSourceItem'];
export type SourceEventLinkStatus = components['schemas']['SourceEventLinkStatus'];
type PiEventPage = components['schemas']['PagedResultOfPiEventView'];

/** Розмір сторінки подій (кожна несе атрибути). */
export const EventsPageSize = 50;

/** Фільтри списку подій. */
export interface PiEventFilters {
  readonly mapId: number | null;
  readonly statuses: readonly SourceEventLinkStatus[];
  readonly element: string;
  readonly name: string;
}

/** Запит подій сутності за вікно. */
export interface PiEventsRef extends RawWindow {
  readonly entityId: number;
  readonly filters: PiEventFilters;
}

/** Рядок запиту: вікно + фільтри (порожні — не надсилаються). */
export function eventsQuery(ref: PiEventsRef, extra: Record<string, string | null> = {}): string {
  const query = new URLSearchParams({ fromUtc: ref.fromUtc, toUtc: ref.toUtc });
  const { mapId, statuses, element, name } = ref.filters;
  if (mapId !== null) query.set('mapId', String(mapId));
  for (const status of statuses) query.append('status', status);
  if (element.trim().length > 0) query.set('element', element.trim());
  if (name.trim().length > 0) query.set('name', name.trim());
  for (const [key, value] of Object.entries(extra)) {
    if (value !== null && value.length > 0) query.set(key, value);
  }

  return query.toString();
}

/** Мапінги подій сутності й періоди, що перетинають вікно. */
export function fetchPiEventMaps(entityId: number, window: RawWindow): Promise<PiEventMapsView> {
  const query = new URLSearchParams({
    fromUtc: window.fromUtc,
    toUtc: window.toUtc,
  });

  return apiFetch<PiEventMapsView>(`/api/v1/sources/${String(entityId)}/raw-data/events/maps?${query.toString()}`);
}

/** Сторінка подій, новіші першими. */
export function fetchPiEvents(ref: PiEventsRef, cursor: string | null): Promise<PiEventPage> {
  const query = eventsQuery(ref, { cursor, limit: String(EventsPageSize) });

  return apiFetch<PiEventPage>(`/api/v1/sources/${String(ref.entityId)}/raw-data/events?${query}`);
}

/** Журнал видалень рядків подій за вікно. */
export function fetchPiEventRemovals(entityId: number, window: RawWindow): Promise<PiEventRemovalsView> {
  const query = new URLSearchParams({
    fromUtc: window.fromUtc,
    toUtc: window.toUtc,
  });

  return apiFetch<PiEventRemovalsView>(
    `/api/v1/sources/${String(entityId)}/raw-data/events/removed?${query.toString()}`,
  );
}

/**
 * CSV подій × полів.
 *
 * ⛔ `fetch` → blob: відмову (`422` понад стелю) браузер за посиланням показав би сирим JSON.
 * ⚠ `method: 'GET'` явно — сторож `EndpointCoverageTests` виводить метод із назви функції.
 */
export async function fetchPiEventExport(ref: PiEventsRef): Promise<RawExportFile> {
  const response = await apiFetchResponse(
    `/api/v1/sources/${String(ref.entityId)}/raw-data/events/export.csv?${eventsQuery(ref)}`,
    { method: 'GET' },
  );

  return {
    blob: await response.blob(),
    fileName: fileNameOf(response.headers.get('Content-Disposition')),
  };
}

/**
 * Момент у поясі проєкту (`timeZoneId`), мовою інтерфейсу; невідомий браузеру пояс — сирий локальний рядок сервера.
 */
export function formatProjectTime(utc: string | null, timeZoneId: string, local: string | null): string {
  if (utc === null) return '';

  try {
    return formatDateTime(utc, {
      dateStyle: 'medium',
      timeStyle: 'medium',
      timeZone: timeZoneId,
    });
  } catch {
    return local ?? utc;
  }
}

/** Той самий момент в UTC — видимим текстом і в підказці. */
export function formatUtcMoment(utc: string | null): string {
  return utc === null
    ? ''
    : `${formatDateTime(utc, { dateStyle: 'medium', timeStyle: 'medium', timeZone: 'UTC' })} UTC`;
}

/** Значення PI для показу: число дослівно, інакше текст. */
export function piValueText(attribute: PiEventAttributeView): string {
  // ⚠ decimal(34,16) сервер віддає рядком із хвостовими нулями — показ без них (як у сирих точках D-463).
  if (attribute.piValueNumeric !== null) return trimDecimal(attribute.piValueNumeric);
  return attribute.piValueText ?? '—';
}

/** Десяткове число дослівно, без хвостових нулів після коми. */
export function trimDecimal(value: string): string {
  return value.includes('.') ? value.replace(/0+$/, '').replace(/\.$/, '') : value;
}
