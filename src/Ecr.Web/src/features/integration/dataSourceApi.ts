import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/** Джерело даних — підключення, з якого беруться сутності збору (ФВ-14.3). */
export type DataSource = components['schemas']['DataSourceView'];
export type SaveDataSourceBody = components['schemas']['SaveDataSourceRequest'];
export type DataSourceTestResult = components['schemas']['DataSourceTestResult'];

/*
 * ⛔ Поля секрету тут немає — і не через забудькуватість: рішення людини на
 * Q15-06 прибрало сховище секретів узагалі (джерела ходять під службовим
 * обліковим записом). У відповіді є лише `hasSecret`; дії «замінити секрет»
 * не існує ні тут, ні на сервері.
 *
 * ⛔ Адреси записані повністю, а не збираються з помічника — той самий прийом,
 * що й у `scheduleApi.ts`: сторож `Кожна_дія_сервера_має_споживача_в_інтерфейсі`
 * шукає літерал `/api/v1/…` разом із методом поруч.
 */

/**
 * `If-Match` із версією рядка — той самий контракт, що в `scheduleApi.ts`.
 *
 * ⛔ Обов'язковий для зміни й видалення: без нього сервер відповідає `422`, а
 * якщо з'єднання встигли змінити — `409 ECR-JOB-0409`. Передавати треба
 * `rowVersion` того рядка, який ПОКАЗАЛИ людині, а не перечитаний перед збереженням.
 */
function ifMatch(rowVersion: string): HeadersInit {
  return { 'If-Match': `"${rowVersion}"` };
}

export function listDataSources(): Promise<DataSource[]> {
  return apiFetch<DataSource[]>('/api/v1/data-sources');
}

export function createDataSource(body: SaveDataSourceBody): Promise<DataSource> {
  return apiFetch<DataSource>('/api/v1/data-sources', {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

/** Змінює джерело. Код не змінюється: на нього спираються сутності збору. */
export function updateDataSource(
  id: number,
  body: SaveDataSourceBody,
  rowVersion: string,
): Promise<DataSource> {
  return apiFetch<DataSource>(`/api/v1/data-sources/${id}`, {
    method: 'PUT',
    headers: ifMatch(rowVersion),
    body: JSON.stringify(body),
  });
}

/**
 * Прибирає джерело, на яке ніщо не спирається.
 *
 * ⛔ Джерело із сутностями збору або розкладами сервер НЕ видаляє — `409`
 * (`ECR-JOB-0409`) із лічильниками. Таке джерело вимикають:
 * `updateDataSource(id, { ...body, isActive: false }, rowVersion)`.
 */
export function deleteDataSource(id: number, rowVersion: string): Promise<void> {
  return apiFetch<void>(`/api/v1/data-sources/${id}`, {
    method: 'DELETE',
    headers: ifMatch(rowVersion),
  });
}

/**
 * Перевіряє з'єднання з джерелом.
 *
 * ⚠ Причина обов'язкова: спроба йде в журнал безпеки. Відмова джерела приходить
 * як `{ ok: false, error }` зі статусом `200` — це відповідь, а не збій запиту;
 * а `409` означає, що проба цього джерела вже виконується.
 */
export function testDataSource(id: number, reason: string): Promise<DataSourceTestResult> {
  return apiFetch<DataSourceTestResult>(`/api/v1/data-sources/${id}/test`, {
    method: 'POST',
    body: JSON.stringify({ reason }),
  });
}
