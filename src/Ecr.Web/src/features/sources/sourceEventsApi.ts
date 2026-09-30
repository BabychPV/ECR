import { apiEnqueue, apiFetch } from '@/api/client';
import type { AcceptedJob } from '@/api/client';
import type { components } from '@/api/schema';

/**
 * Клієнт API подій джерела (PI Event Frames → рядки динамічних таблиць): каталог шаблонів, проба, мапінг подій,
 * таблиця подій і «Отримати з PI зараз» (HSE301 A6, `FEATURE-HSE301-VIEW` §4.7).
 *
 * ⚠ Лише функції й типи, без інтерфейсу: екрани (вкладка «Події з PI» на сторінці джерела, форма мапінгу подій)
 * роблять окремо, проти цього контракту. Файл не імпортується з маршрутів — бюджет чанка `DocumentPage` не рухається.
 *
 * ⛔ Кожна адреса записана ПОВНІСТЮ одним літералом: сторож `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає
 * літерал `/api/v1/…`, і зібрана з частин адреса для нього не існує (той самий прийом, що в `piafProbeApi.ts`).
 */

/** Шаблон подій джерела з атрибутами (`GET …/event-templates`). */
export type SourceEventTemplate = components['schemas']['SourceEventTemplate'];

/** Мапінг подій «шаблон → динамічна таблиця документа» з полями й відповідностями значень. */
export type SourceEventMap = components['schemas']['SourceEventMapDto'];

/**
 * Поля запиту, що можуть бути `null`, у тілі необов'язкові: згенерований тип вимагає їх явно (сервер віддає
 * `nullable` як «обов'язкове, але порожнє»), а на вході відсутнє поле сервер читає так само, як `null`.
 */
type NullableOptional<T> = {
  [K in keyof T as null extends T[K] ? K : never]?: T[K];
} & {
  [K in keyof T as null extends T[K] ? never : K]: T[K];
};

/** Поле мапінгу подій «атрибут → колонка» у запиті. */
export type SourceEventFieldInput = NullableOptional<
  components['schemas']['SourceEventFieldInput']
>;

/** Створення мапінгу подій. */
export type CreateSourceEventMapRequest = Omit<
  NullableOptional<components['schemas']['CreateSourceEventMapCommand']>,
  'fields'
> & { fields: SourceEventFieldInput[] };

/** Повна заміна налаштувань мапінгу подій (`isActive: false` — пауза). */
export type UpdateSourceEventMapRequest = Omit<
  NullableOptional<components['schemas']['UpdateSourceEventMapCommand']>,
  'fields'
> & { fields: SourceEventFieldInput[] };

/** Що читати пробою подій. */
export type ProbeSourceEventsRequest = NullableOptional<
  components['schemas']['SourceEventProbeRequest']
>;

/** Наслідок пробного читання подій. */
export type ProbeSourceEventsResult = components['schemas']['SourceEventProbeResult'];

/** Рядок таблиці подій: зв'язок «подія ↔ рядок документа» зі станом. */
export type SourceEventRow = components['schemas']['SourceEventRowDto'];

/** Сторінка таблиці подій. */
export type SourceEventsPage = components['schemas']['PagedResultOfSourceEventRowDto'];

/** Стан синхронізації події (`Synced`, `Open`, `Missing`, `PeriodClosed`, `PeriodChanged`, `PeriodNotOpen`, `Unmapped`, `RowLimit`). */
export type SourceEventLinkStatus = components['schemas']['SourceEventLinkStatus'];

/** Фільтри таблиці подій; усе необов'язкове. */
export interface SourceEventsQuery {
  /** Лише цей мапінг. */
  mapId?: number;
  /** Лише цей документ. */
  documentId?: number;
  /** Лише ці стани; порожньо — усі. */
  status?: readonly SourceEventLinkStatus[];
  /** Початок події не раніше (UTC, ISO). */
  fromUtc?: string;
  /** Початок події раніше (UTC, ISO). */
  toUtc?: string;
  /** Лише цей період (`YYYYMM`). */
  periodKey?: number;
  /** Курсор наступної сторінки (`nextCursor` попередньої). */
  cursor?: string;
  /** Розмір сторінки 1..500; типово 50. */
  limit?: number;
}

/** Каталог шаблонів подій джерела; без налаштованого запиту сервер відмовляє `422 ECR-INT-0422`. */
export function fetchEventTemplates(dataSourceId: number): Promise<SourceEventTemplate[]> {
  return apiFetch<SourceEventTemplate[]>(`/api/v1/data-sources/${dataSourceId}/event-templates`);
}

/** Пробне читання подій шаблону без запису; вікно типово 30 днів, стеля 20 подій (до 100). */
export function probeSourceEvents(
  dataSourceId: number,
  request: ProbeSourceEventsRequest,
): Promise<ProbeSourceEventsResult> {
  return apiFetch<ProbeSourceEventsResult>(`/api/v1/data-sources/${dataSourceId}/probe-events`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
}

/** Таблиця подій сутності: новіші за початком першими; невидимі документи не існують. */
export function fetchSourceEvents(
  sourceEntityId: number,
  query: SourceEventsQuery = {},
): Promise<SourceEventsPage> {
  const params = new URLSearchParams();
  if (query.mapId !== undefined) params.set('mapId', String(query.mapId));
  if (query.documentId !== undefined) params.set('documentId', String(query.documentId));
  for (const status of query.status ?? []) params.append('status', status);
  if (query.fromUtc !== undefined) params.set('fromUtc', query.fromUtc);
  if (query.toUtc !== undefined) params.set('toUtc', query.toUtc);
  if (query.periodKey !== undefined) params.set('periodKey', String(query.periodKey));
  if (query.cursor !== undefined) params.set('cursor', query.cursor);
  if (query.limit !== undefined) params.set('limit', String(query.limit));

  // ⚠ Рядок запиту — після літерального `?`: сторож відрізає його й бачить чисту адресу; порожній запит (`…events?`) сервер приймає.
  return apiFetch<SourceEventsPage>(
    `/api/v1/sources/${sourceEntityId}/source-events?${params.toString()}`,
  );
}

/**
 * «Отримати з PI зараз»: ставить синхронізацію подій сутності в чергу (`202`, `jobId`). Повторне натискання
 * зливається з задачею, що вже є; без активного мапінгу подій — `422`.
 */
export function syncSourceEvents(sourceEntityId: number): Promise<AcceptedJob> {
  return apiEnqueue(`/api/v1/sources/${sourceEntityId}/source-events/sync`);
}

/** Мапінги подій; за `sourceEntityId` — лише сутності. */
export function fetchSourceEventMaps(sourceEntityId?: number): Promise<SourceEventMap[]> {
  const params = new URLSearchParams();
  if (sourceEntityId !== undefined) params.set('sourceEntityId', String(sourceEntityId));

  return apiFetch<SourceEventMap[]>(`/api/v1/source-event-maps?${params.toString()}`);
}

/** Один мапінг подій. */
export function fetchSourceEventMap(id: number): Promise<SourceEventMap> {
  return apiFetch<SourceEventMap>(`/api/v1/source-event-maps/${id}`);
}

/** Заводить мапінг подій; обов'язкові `$start` і `$end` на Date-колонки. */
export function createSourceEventMap(
  request: CreateSourceEventMapRequest,
): Promise<SourceEventMap> {
  return apiFetch<SourceEventMap>('/api/v1/source-event-maps', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
}

/** Повна заміна налаштувань мапінгу подій: режим об'єму, звуження, поля, пауза. */
export function updateSourceEventMap(
  id: number,
  request: UpdateSourceEventMapRequest,
): Promise<SourceEventMap> {
  return apiFetch<SourceEventMap>(`/api/v1/source-event-maps/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
}

/** Видаляє мапінг подій, за яким нічого не синхронізовано; зі зв'язками — `409 ECR-INT-0409`, вихід — пауза. */
export function deleteSourceEventMap(id: number): Promise<void> {
  return apiFetch<void>(`/api/v1/source-event-maps/${id}`, {
    method: 'DELETE',
  });
}
