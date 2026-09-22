import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/**
 * Заповненість однієї таблиці документа (`BE-10`).
 *
 * ⚠ Тип береться зі ЗГЕНЕРОВАНОЇ схеми, а не пишеться руками. Саме рукописні
 * типи відповідей і дали `A7-34`/`A7-35`/`A7-36`: поле, назване інакше, ніж на
 * сервері, нічого не ламає — воно просто `undefined`, а компілятор обіцяв
 * число.
 */
export type TableStatus = components['schemas']['TableStatusDto'];

/**
 * Статус таблиць документа за період.
 *
 * ⛔ Адреса записана ПОВНІСТЮ і поруч із викликом `apiFetch`, а не збирається
 * з помічника. Сторож `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає в
 * коді клієнта саме літерали `/api/v1/…` разом із функцією поруч; винесений у
 * помічник префікс зробив би дію «недосяжною з інтерфейсу» для сторожа.
 */
export function tableStatus(documentId: number, periodKey: number): Promise<TableStatus[]> {
  return apiFetch<TableStatus[]>(
    `/api/v1/documents/${String(documentId)}/tables/status?periodKey=${String(periodKey)}`,
  );
}

/**
 * Заповненість таблиць документа.
 *
 * ⚠ Ключ запиту ОКРЕМИЙ від `['document-tables', …]`: структура документа
 * читається один раз на відкриття, а заповненість змінюється після кожного
 * запису комірок. Спільний ключ означав би або перечитування структури заради
 * лічильника, або лічильник із моменту відкриття сторінки.
 */
export function useTableStatus(
  documentId: number,
  periodKey: number,
): UseQueryResult<TableStatus[]> {
  return useQuery({
    queryKey: ['document-tables-status', documentId, periodKey],
    queryFn: () => tableStatus(documentId, periodKey),
  });
}

/** Скільки таблиць заповнено повністю і скільки їх усього. */
export interface FillSummary {
  /** Таблиці, у яких заповнені всі комірки, що їх має заповнити людина. */
  filled: number;

  /** Усього таблиць у документі за цей період. */
  total: number;

  /**
   * Чи є хоч одне порушення рівня `Error`.
   *
   * ⛔ `null` — документ за цей період НЕ ПЕРЕВІРЯЛИ, і це не те саме, що
   * «порушень немає». Зелений стан під неперевіреним документом — та сама
   * неправда, що й `A7-28`: на нього спираються, подаючи звітність.
   */
  hasErrors: boolean | null;
}

/**
 * Зводить перелік таблиць у «68 з 91».
 *
 * ⚠ Таблиця БЕЗ жодної вхідної комірки (`inputCells === 0` — усе формульне
 * або закрите правилом періоду) вважається заповненою: заповнювати в ній
 * нічого, і лишити її вічно «незаповненою» означало б, що документ ніколи не
 * дійде до 100 %.
 */
export function summarize(tables: readonly TableStatus[]): FillSummary {
  const validated = tables.some((table) => table.errorCount !== null);

  return {
    filled: tables.filter((table) => table.filledCells >= table.inputCells).length,
    total: tables.length,
    hasErrors: validated ? tables.some((table) => (table.errorCount ?? 0) > 0) : null,
  };
}

/**
 * Видаляє документ-чернетку (право `Document.Delete`).
 *
 * ⚠ Не чернетка (хоч один аркуш подано, погоджено, відхилено або вже був у
 * погодженні) — `409` `ECR-DOC-0409`, причина в `messageKey`; чужий — `404`.
 */
export function deleteDocument(documentId: number): Promise<void> {
  return apiFetch<void>(`/api/v1/documents/${String(documentId)}`, { method: 'DELETE' });
}

/** Рядок переліку документів (`hasLateEdits` — `BE-09b`); тип — зі згенерованої схеми. */
export type DocumentSummary = components['schemas']['DocumentSummary'];

/** Сторінка переліку документів. */
export type DocumentListPage = components['schemas']['PagedResultOfDocumentSummary'];

/** Значення фільтра `state` — імена `DocumentStatus` на сервері; інше сервер відхиляє `422`. */
export type DocumentStateFilter = 'Draft' | 'Submitted' | 'Approved' | 'Rejected';

/** Параметри переліку документів (`BE-09b`). */
export interface DocumentListParams {
  periodKey: number | null;
  cursor?: string | null;
  /** ⚠ Лише разом із `periodKey`: без періоду стан не визначений (`D-93`), сервер відповість `422`. */
  state?: DocumentStateFilter | null;
  /** Лише документи, де я автор або подавав аркуш. */
  mine?: boolean;
}

/**
 * Сторінка переліку документів із фільтрами.
 *
 * ⚠ Порожні параметри НЕ йдуть у запит: `state=` без значення сервер читає як
 * «без фільтра», але `mine=false` у адресі — лише шум у посиланні.
 */
export function listDocuments(params: DocumentListParams): Promise<DocumentListPage> {
  const query = new URLSearchParams({ limit: '50' });
  if (params.periodKey !== null) query.set('periodKey', String(params.periodKey));
  if (params.cursor) query.set('cursor', params.cursor);
  if (params.state) query.set('state', params.state);
  if (params.mine) query.set('mine', 'true');

  return apiFetch<DocumentListPage>(`/api/v1/documents?${query.toString()}`);
}

/** Лічильники над переліком документів (`BE-09`); тип — зі згенерованої схеми. */
export type DocumentListSummary = components['schemas']['DocumentListSummaryResponse'];

/** Зведення переліку за період. Адреса — повним літералом: її шукає сторож споживачів. */
export function documentListSummary(periodKey: number): Promise<DocumentListSummary> {
  return apiFetch<DocumentListSummary>(`/api/v1/documents/summary?periodKey=${String(periodKey)}`);
}

/**
 * Зведення переліку документів.
 *
 * ⚠ Без періоду запит НЕ йде: стан документа поза періодом не визначений
 * (`D-93`), і сервер на це відповів би `422`.
 *
 * ⚠ Ключ починається з `'documents'` навмисно: усе, що інвалідовує перелік
 * (створення документа), тим самим префіксом оновлює і смугу над ним.
 */
export function useDocumentListSummary(
  periodKey: number | null,
): UseQueryResult<DocumentListSummary> {
  return useQuery({
    queryKey: ['documents', 'summary', periodKey],
    queryFn: () => documentListSummary(periodKey ?? 0),
    enabled: periodKey !== null,
  });
}

/** Право, під яким сервер приймає зміну бізнес-ключа документа (ФВ-3.9). */
export const ChangeDocumentKeyPermission = 'Document.ChangeKey';

/**
 * Найдовший бізнес-ключ документа — дзеркало
 * `ChangeDocumentKeyHandler.MaxKeyLength` (колонка `doc.Document.BusinessKey`).
 */
export const BusinessKeyMaxLength = 200;

/** Параметри зміни бізнес-ключа документа. */
export interface ChangeDocumentKeyParams {
  readonly documentId: number;
  /** Новий ключ. */
  readonly businessKey: string;
  /** Ключ, який людина БАЧИТЬ на екрані зараз; розбіжність із чинним — `409 rekeyStale`. */
  readonly expectedBusinessKey: string;
  /** Причина; обов'язкова, лягає в аудит. */
  readonly reason: string;
}

/**
 * Змінює бізнес-ключ документа (ФВ-3.9): право `Document.ChangeKey` + грант
 * Write на проєкт.
 *
 * ⚠ Ключ зайнятий — `409` `err.ECR-DOC-0409.rekeyDuplicate`; поданий чи
 * погоджений аркуш — `409` `err.ECR-DOC-0409.rekeyLocked`; `expectedBusinessKey`
 * розійшовся з чинним (хтось уже змінив ключ) — `409` `err.ECR-DOC-0409.rekeyStale`;
 * причина порожня чи ключ поза 1–200 символів або збігається з чинним — `422`
 * `err.ECR-DOC-0422.rekeyReasonRequired`/`rekeyKeyInvalid`.
 */
export function changeDocumentBusinessKey(params: ChangeDocumentKeyParams): Promise<void> {
  return apiFetch<void>(`/api/v1/documents/${String(params.documentId)}/business-key`, {
    method: 'POST',
    body: JSON.stringify({
      businessKey: params.businessKey,
      expectedBusinessKey: params.expectedBusinessKey,
      reason: params.reason,
    } satisfies components['schemas']['ChangeDocumentKeyRequest']),
  });
}
