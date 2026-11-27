import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/** ✎ D-577 (L3): стан розрахунку документа — застарілі місяці, задачі в черзі/в роботі, останній провал. */
export type DocumentCalcStatus = components['schemas']['DocumentCalcStatusDto'];

/** ✎ D-577 (L6): порожні обов'язкові входи методологій таблиці (по всіх рядках, до стелі сервера). */
export type TableRequiredInputs = components['schemas']['RequiredInputsDto'];

/**
 * ✎ D-577 (L3): стан розрахунку документа за період на екрані. Клієнт опитує його, поки є задача в черзі чи в роботі;
 * сервер тримає короткий кеш застарілості (ключ — склад живих задач), тож опитування дешеве.
 */
export function getDocumentCalcStatus(documentId: number, periodKey: number): Promise<DocumentCalcStatus> {
  return apiFetch<DocumentCalcStatus>(
    `/api/v1/documents/${String(documentId)}/calc-status?periodKey=${String(periodKey)}`,
  );
}

/**
 * ✎ D-577 (L6, Q4): порожні обов'язкові входи таблиці при відкритті — модель «потрібне значення» в сітці. Комірки схованих
 * від читача колонок сервер не повертає; `truncated` — перелік обрізано стелею (`total` — скільки всього).
 */
export function getTableRequiredInputs(documentId: number, tableInstanceId: number): Promise<TableRequiredInputs> {
  return apiFetch<TableRequiredInputs>(
    `/api/v1/documents/${String(documentId)}/tables/${String(tableInstanceId)}/required-inputs`,
  );
}
