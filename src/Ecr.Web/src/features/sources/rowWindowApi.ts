import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/**
 * Клієнт API прив'язок PI за вікном рядка (`ext.RowWindowMap`): «атрибут джерела → колонка, вікно = рядок»
 * (HSE301 A1, `FEATURE-HSE301-VIEW` §4.4).
 *
 * ⚠ Лише функції й типи, без інтерфейсу; файл не імпортується з маршрутів — бюджет чанка `DocumentPage` не
 * рухається.
 *
 * ⛔ Кожна адреса записана ПОВНІСТЮ одним літералом: сторож `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає
 * літерал `/api/v1/…`.
 *
 * ⚠ Decimal у контракті — рядок (`minPercentGood`): і в відповіді, і в запиті.
 */

/** Прив'язка з кодами колонок, джерелами й `rowVersion`. */
export type RowWindowMap = components['schemas']['RowWindowMapDto'];

/** Спосіб згортки вікна. */
export type RowWindowSummary = components['schemas']['RowWindowSummaryKind'];

/** Джерело прив'язки «значення селектора → атрибут». */
export type RowWindowSourceInput = components['schemas']['RowWindowSourceInput'];

/**
 * Поля запиту, що можуть бути `null`, у тілі необов'язкові: згенерований тип вимагає їх явно, а відсутнє поле сервер
 * читає так само, як `null`.
 */
type NullableOptional<T> = {
  [K in keyof T as null extends T[K] ? K : never]?: T[K];
} & {
  [K in keyof T as null extends T[K] ? never : K]: T[K];
};

/** Створення прив'язки. */
export type CreateRowWindowMapRequest = Omit<
  NullableOptional<components['schemas']['CreateRowWindowMapCommand']>,
  'sources'
> & { sources: RowWindowSourceInput[] };

/** Повна заміна налаштувань прив'язки (`isActive: false` — пауза). */
export type UpdateRowWindowMapRequest = Omit<
  NullableOptional<components['schemas']['UpdateRowWindowMapCommand']>,
  'sources'
> & { sources: RowWindowSourceInput[] };

/** Прив'язки сутності джерела; без `sourceEntityId` — усі. */
export function fetchRowWindowMaps(sourceEntityId?: number): Promise<RowWindowMap[]> {
  const params = new URLSearchParams();
  if (sourceEntityId !== undefined) params.set('sourceEntityId', String(sourceEntityId));

  // ⚠ Рядок запиту — після літерального `?`: сторож відрізає його й бачить чисту адресу.
  return apiFetch<RowWindowMap[]>(`/api/v1/row-window-maps?${params.toString()}`);
}

/** Одна прив'язка. */
export function fetchRowWindowMap(id: number): Promise<RowWindowMap> {
  return apiFetch<RowWindowMap>(`/api/v1/row-window-maps/${id}`);
}

/** Заводить прив'язку (`201`). */
export function createRowWindowMap(request: CreateRowWindowMapRequest): Promise<RowWindowMap> {
  return apiFetch<RowWindowMap>('/api/v1/row-window-maps', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
}

/** Повна заміна налаштувань: вікно, селектор, згортка, пороги, джерела, пауза; `rowVersion` — оптимістичне блокування. */
export function updateRowWindowMap(id: number, request: UpdateRowWindowMapRequest): Promise<RowWindowMap> {
  return apiFetch<RowWindowMap>(`/api/v1/row-window-maps/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
}

/** Видаляє прив'язку без провенансу; із провенансом — `409 ECR-INT-0409`, вихід — пауза. */
export function deleteRowWindowMap(id: number): Promise<void> {
  return apiFetch<void>(`/api/v1/row-window-maps/${id}`, {
    method: 'DELETE',
  });
}
