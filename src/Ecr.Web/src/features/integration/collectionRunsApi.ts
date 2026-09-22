import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/**
 * Журнал прогонів збору (ФВ-5.23) — `GET /api/v1/collection-runs` і
 * `GET /api/v1/collection-runs/{id}`.
 *
 * ⛔ Право те саме, що на `/admin/sources` (`Integration.View` АБО
 * `Integration.Manage`, `routes.ts:adminSources`): сервер
 * (`ListCollectionRunsHandler`/`GetCollectionRunHandler`) вимагає рівно ту
 * саму пару прав, що й `ListDataSourcesHandler`. Тому журнал живе СЕКЦІЄЮ на
 * тій самій сторінці (`SourcesPage.tsx`), а не власним маршрутом: власний
 * маршрут ввів би НОВИЙ ключ `labelKey` у `routes.ts`, а
 * `EndpointCoverageTests.RouteLabelKeys` (`tests/Ecr.Architecture.Tests`) —
 * файл поза дозволеним для цієї задачі списком — статично перелічує кожен
 * ключ цього реєстру. Новий ключ, не дописаний туди, валить сторожа
 * `Перелік_ключів_через_змінну_не_застарів`, а дописати його ця задача не
 * може. Секція на наявному маршруті цього не потребує взагалі.
 */
export type CollectionRunView = components['schemas']['CollectionRunView'];
export type CollectionRunDetail = components['schemas']['CollectionRunDetail'];
export type CollectionRunCoverage = components['schemas']['CollectionRunCoverage'];
export type CollectionRunPage = components['schemas']['PagedResultOfCollectionRunView'];

/** Стани прогону — рівно перелік сервера (`ListCollectionRunsHandler.KnownStates`). */
export const CollectionRunStates = ['Running', 'Succeeded', 'Degraded', 'Failed'] as const;
export type CollectionRunState = (typeof CollectionRunStates)[number];

/** Фільтр журналу — усі поля необов'язкові, `null`/відсутнє поле в запит не йде. */
export interface CollectionRunFilters {
  readonly dataSource?: number | null | undefined;
  readonly entity?: number | null | undefined;
  readonly state?: string | null | undefined;

  /** UTC, включно (`ISO 8601`). */
  readonly from?: string | null | undefined;

  /** UTC, виключно (`ISO 8601`). */
  readonly to?: string | null | undefined;
}

/** Розмір сторінки за замовчуванням — той самий, що бере сервер на `limit=0`. */
export const CollectionRunsDefaultLimit = 50;

/**
 * Ключ запиту переліку — стабільний масив полів фільтра, БЕЗ курсора.
 *
 * ⚠ `useInfiniteQuery` (`CollectionRunsPanel.tsx`) сам відповідає за курсор
 * через `pageParam`: один ключ на весь набір сторінок фільтра, інакше зміна
 * сторінки заводила б окремий незалежний запит замість «дочитати далі».
 */
export function collectionRunsQueryKey(filters: CollectionRunFilters): readonly unknown[] {
  return [
    'collectionRuns',
    filters.dataSource ?? null,
    filters.entity ?? null,
    filters.state ?? null,
    filters.from ?? null,
    filters.to ?? null,
  ];
}

/** Ключ запиту деталі одного прогону. */
export function collectionRunDetailQueryKey(id: number): readonly unknown[] {
  return ['collectionRunDetail', id];
}

/** Сторінка журналу, новіші першими. */
export function listCollectionRuns(
  filters: CollectionRunFilters,
  cursor: string | null,
): Promise<CollectionRunPage> {
  const query = new URLSearchParams();

  if (filters.dataSource !== null && filters.dataSource !== undefined) {
    query.set('dataSource', String(filters.dataSource));
  }
  if (filters.entity !== null && filters.entity !== undefined) {
    query.set('entity', String(filters.entity));
  }
  if (filters.state !== null && filters.state !== undefined && filters.state.length > 0) {
    query.set('state', filters.state);
  }
  if (filters.from !== null && filters.from !== undefined && filters.from.length > 0) {
    query.set('from', filters.from);
  }
  if (filters.to !== null && filters.to !== undefined && filters.to.length > 0) {
    query.set('to', filters.to);
  }
  if (cursor !== null && cursor.length > 0) {
    query.set('cursor', cursor);
  }
  query.set('limit', String(CollectionRunsDefaultLimit));

  return apiFetch<CollectionRunPage>(`/api/v1/collection-runs?${query.toString()}`);
}

/** Прогін із текстом помилки й покриттям (до 500 інтервалів). */
export function getCollectionRunDetail(id: number): Promise<CollectionRunDetail> {
  return apiFetch<CollectionRunDetail>(`/api/v1/collection-runs/${id}`);
}
