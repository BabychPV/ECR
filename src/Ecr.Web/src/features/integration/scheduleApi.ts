import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/** Розклад збору для сутності джерела (ФВ-14.3). */
export type CollectionSchedule = components['schemas']['CollectionScheduleView'];
export type CreateCollectionScheduleBody = components['schemas']['CreateCollectionScheduleRequest'];
export type UpdateCollectionScheduleBody = components['schemas']['UpdateCollectionScheduleRequest'];

/*
 * ⛔ Адреси записані повністю, а не збираються з помічника — той самий прийом,
 * що й `features/notifications/api.ts`: сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає літерал `/api/v1/…`
 * разом із методом поруч.
 *
 * ⚠ Екрана розкладу ще НЕМАЄ — модуль поки має єдиного споживача, власний тест.
 * Сказано навмисно: коментар, що обіцяє неіснуючий екран, дорожчий за відсутній.
 */

/**
 * `If-Match` із версією рядка, прочитаної перед правкою.
 *
 * ⛔ Заголовок обов'язковий для зміни й видалення: сервер відповідає
 * `409 ECR-JOB-0409`, якщо розклад устигли змінити, і `422`, якщо заголовка
 * немає взагалі. Передавати треба саме `rowVersion` того рядка, який показали
 * користувачеві, — а не перечитаний перед самим збереженням.
 */
function ifMatch(rowVersion: string): HeadersInit {
  return { 'If-Match': `"${rowVersion}"` };
}

/**
 * Розклади збору; `dataSourceCode` — лише розклади сутностей цього з'єднання
 * (поле `dataSourceCode` рядка). Фільтрує сервер, до стелі переліку;
 * невідомий код — порожній перелік, а не помилка.
 */
export function listCollectionSchedules(dataSourceCode?: string): Promise<CollectionSchedule[]> {
  return apiFetch<CollectionSchedule[]>(
    dataSourceCode
      ? `/api/v1/collection-schedules?dataSource=${encodeURIComponent(dataSourceCode)}`
      : '/api/v1/collection-schedules',
  );
}

/**
 * Заводить розклад для сутності джерела, у якої його ще немає.
 *
 * ⛔ Без `If-Match`: створення нічого не перезаписує, і версії рядка, якого ще
 * немає, взяти нізвідки. Сутність із уже наявним розкладом — `409`
 * (`ECR-JOB-0409`), а не другий рядок: два розклади на одну сутність означають
 * подвійний збір.
 */
export function createCollectionSchedule(
  body: CreateCollectionScheduleBody,
): Promise<CollectionSchedule> {
  return apiFetch<CollectionSchedule>('/api/v1/collection-schedules', {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

/** Змінює cron і вмикає/вимикає розклад. Невалідний cron — `422`, ДО запису. */
export function updateCollectionSchedule(
  id: number,
  body: UpdateCollectionScheduleBody,
  rowVersion: string,
): Promise<CollectionSchedule> {
  return apiFetch<CollectionSchedule>(`/api/v1/collection-schedules/${id}`, {
    method: 'PUT',
    headers: ifMatch(rowVersion),
    body: JSON.stringify(body),
  });
}

/** Прибирає розклад; сервер сам знімає задачу з планувальника. */
export function deleteCollectionSchedule(id: number, rowVersion: string): Promise<void> {
  return apiFetch<void>(`/api/v1/collection-schedules/${id}`, {
    method: 'DELETE',
    headers: ifMatch(rowVersion),
  });
}
