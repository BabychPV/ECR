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
