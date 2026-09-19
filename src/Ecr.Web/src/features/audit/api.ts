import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import type { CellChangePage } from '@/api/types';

/*
 * ⛔ Адреса записана ПОВНІСТЮ і поруч із викликом `apiFetch`, а не збирається
 * з помічника. Сторож `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає в
 * коді клієнта саме літерали `/api/v1/…` разом із функцією поруч; винесений у
 * помічник префікс зробив би дію «недосяжною з інтерфейсу» для сторожа.
 */

/** Походження зміни — ті самі чотири значення, що пише `aud.CellChange.Origin`. */
export const cellChangeOrigins = ['UserEdit', 'Import', 'Recalculation', 'Migration'] as const;

/** Фільтр журналу змін комірок. */
export interface CellChangeFilter {
  /** Початок вікна (`YYYY-MM-DD` або ISO); **обов'язковий**. */
  readonly from: string;
  /** Кінець вікна; **обов'язковий**. */
  readonly to: string;
  readonly documentId?: number | null;
  readonly rowKey?: string | null;
  readonly columnDefId?: number | null;
  /** Автор зміни — `UserId`, не SID. */
  readonly author?: number | null;
  readonly origin?: string | null;
  readonly lateOnly?: boolean;
  readonly limit?: number;
  readonly cursor?: string | null;
}

/**
 * Фільтр адресує РІВНО ОДНУ комірку — документ, рядок і колонка разом.
 *
 * ⚠ Це не косметика: сервер на такий запит вимагає `Document.View` замість
 * `Security.ViewAudit` і дозволяє вікно в 13 місяців замість 92 днів (`D15-16`).
 * Клієнт мусить знати різницю, щоб не показувати «вікно завелике» там, де
 * сервер його прийме.
 */
export function isSingleCell(filter: CellChangeFilter): boolean {
  return (
    filter.documentId !== null &&
    filter.documentId !== undefined &&
    filter.rowKey !== null &&
    filter.rowKey !== undefined &&
    filter.rowKey.length > 0 &&
    filter.columnDefId !== null &&
    filter.columnDefId !== undefined
  );
}

/**
 * Рядок запиту журналу.
 *
 * ⛔ Через `URLSearchParams`, а не склеюванням: `rowKey` — довільний текст із
 * шаблону, і `&`, `#` чи пробіл у ньому інакше обірвали б адресу. Саме на
 * незакодованому `#` уже падав `smoke.ps1` (крок 17, CLAUDE.md).
 *
 * ⚠ Порожні значення НЕ потрапляють у запит зовсім. `?rowKey=` сервер трактує
 * як відсутній фільтр, але адреса з порожніми хвостами накопичує їх і стає
 * нечитабельною — а саме її людина надсилає колезі.
 */
export function cellChangesQuery(filter: CellChangeFilter): string {
  const params = new URLSearchParams();

  params.set('from', filter.from);
  params.set('to', filter.to);
  params.set('limit', String(filter.limit ?? 100));

  if (filter.documentId !== null && filter.documentId !== undefined) {
    params.set('documentId', String(filter.documentId));
  }

  if (filter.rowKey !== null && filter.rowKey !== undefined && filter.rowKey.length > 0) {
    params.set('rowKey', filter.rowKey);
  }

  if (filter.columnDefId !== null && filter.columnDefId !== undefined) {
    params.set('columnDefId', String(filter.columnDefId));
  }

  if (filter.author !== null && filter.author !== undefined) {
    params.set('author', String(filter.author));
  }

  if (filter.origin !== null && filter.origin !== undefined && filter.origin.length > 0) {
    params.set('origin', filter.origin);
  }

  // ⚠ `lateOnly=false` не надсилається: булеве за замовчуванням і так `false`,
  // а параметр у адресі виглядав би як свідомо обраний фільтр.
  if (filter.lateOnly === true) {
    params.set('lateOnly', 'true');
  }

  if (filter.cursor !== null && filter.cursor !== undefined && filter.cursor.length > 0) {
    params.set('cursor', filter.cursor);
  }

  return params.toString();
}

/** Сторінка журналу структурних змін (`BE-16`). */
export type StructureChangePage = components['schemas']['PagedResultOfStructureChangeView'];

/** Фільтр журналу структурних змін; вікно **обов'язкове**, як у журналі комірок. */
export interface StructureChangeFilter {
  readonly from: string;
  readonly to: string;
  readonly entityType?: string | null;
  /** Автор зміни — `UserId`, не SID. */
  readonly changedByUserId?: number | null;
  readonly limit?: number;
  readonly cursor?: string | null;
}

/** Рядок запиту журналу структурних змін — ті самі правила, що в `cellChangesQuery`. */
export function structureChangesQuery(filter: StructureChangeFilter): string {
  const params = new URLSearchParams();

  params.set('from', filter.from);
  params.set('to', filter.to);
  params.set('limit', String(filter.limit ?? 100));

  if (filter.entityType !== null && filter.entityType !== undefined && filter.entityType.length > 0) {
    params.set('entityType', filter.entityType);
  }

  if (filter.changedByUserId !== null && filter.changedByUserId !== undefined) {
    params.set('changedByUserId', String(filter.changedByUserId));
  }

  if (filter.cursor !== null && filter.cursor !== undefined && filter.cursor.length > 0) {
    params.set('cursor', filter.cursor);
  }

  return params.toString();
}

/** Сторінка загального журналу структурних змін (`BE-16`). */
export function useStructureChanges(
  filter: StructureChangeFilter,
): UseQueryResult<StructureChangePage> {
  const query = structureChangesQuery(filter);

  return useQuery({
    queryKey: ['audit-structure', query],
    queryFn: () => apiFetch<StructureChangePage>(`/api/v1/audit/structure?${query}`),
  });
}

/**
 * Сторінка журналу змін комірок.
 *
 * ⛔ Вікно `from`/`to` — обов'язкове, і хук не вміє його не передати:
 * `aud.CellChange` партиційована за `ChangedAt`, і запит без меж пішов би по
 * ВСІХ партиціях, включно з архівними. Це не оптимізація — це різниця між
 * «повільно» і «сервер зайнятий».
 *
 * ⚠ Той самий хук обслуговує і вкладку History інспектора комірки: історія
 * однієї комірки — це фільтр `documentId + rowKey + columnDefId`, а не окремий
 * маршрут (див. `isSingleCell`).
 */
export function useCellChanges(
  filter: CellChangeFilter,
  enabled = true,
): UseQueryResult<CellChangePage> {
  const query = cellChangesQuery(filter);

  return useQuery({
    enabled,
    queryKey: ['audit-cells', query],
    queryFn: () => apiFetch<CellChangePage>(`/api/v1/audit/cells?${query}`),
  });
}
