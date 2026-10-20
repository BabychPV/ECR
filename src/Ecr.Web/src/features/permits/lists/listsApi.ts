import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/*
 * ⛔ Адреса кожної дії записана повністю (літерал `/api/v1/…` разом із методом поруч): так її читає сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` (Permits, PM-08e).
 */

export type PermitListDto = components['schemas']['PermitListDto'];
export type PermitListUseDto = components['schemas']['PermitListUseDto'];
export type PermitListDetail = components['schemas']['PermitListDetailDto'];
export type PermitListValue = components['schemas']['PermitListValueDto'];
export type PermitListChange = components['schemas']['PermitListItemChange'];

/**
 * Усі списки вибору дозволів ОДНИМ запитом: спільні довідники, списки «лише для типу», таблиці того самого дозволу — зі
 * стовпцями-споживачами й лічильником значень. Право `Registry.View`. Замінює читання визначення кожного типу окремо.
 */
export function listPermitLists(): Promise<PermitListDto[]> {
  return apiFetch<PermitListDto[]>('/api/v1/permit-lists');
}

/** Список зі значеннями за ключем (`shared:…`, `type:…`, `permit:…`). `404 ECR-PRM-0404`, якщо списку немає. */
export function getPermitList(key: string): Promise<PermitListDetail> {
  return apiFetch<PermitListDetail>(`/api/v1/permit-lists/${encodeURIComponent(key)}`);
}

/**
 * Додає, змінює й прибирає значення списку (усе або нічого). Спільний довідник — право `Registry.EditData`, «лише для
 * типу» — `Permit.EditType` (`403` інакше); таблиця того самого дозволу — `422 ECR-PRM-0422`.
 */
export function changePermitListItems(key: string, changes: readonly PermitListChange[]): Promise<PermitListDetail> {
  return apiFetch<PermitListDetail>(`/api/v1/permit-lists/${encodeURIComponent(key)}/items`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ changes }),
  });
}
