import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/** Сутність збору у відповіді на заведення чи прив'язку (`ФВ-13.11`). */
export type SourceEntity = components['schemas']['SourceEntityDto'];
export type CreateSourceEntityBody = components['schemas']['CreateSourceEntityRequest'];

/** Ключ кешу переліку сутностей — той самий, що в таблиці й вкладці розкладу. */
export const SourceEntitiesQueryKey = ['sources'] as const;

/*
 * ⛔ Адреси записані повністю, а не збираються з помічника — той самий прийом,
 * що й `scheduleApi.ts`: сторож `Кожна_дія_сервера_має_споживача_в_інтерфейсі`
 * шукає літерал `/api/v1/…` разом із методом поруч.
 *
 * Споживач — вкладка Entities шухляди з'єднання (`SourceEntitiesTab.tsx`) і
 * форма з каталогу (`AddSourceEntityModal.tsx`).
 */

/**
 * Заводить сутність збору з позиції каталогу джерела.
 *
 * ⚠ Код уже є в з'єднанні — `409 ECR-INT-0409` (`sourceEntityDuplicate`),
 * а не другий рядок.
 */
export function createSourceEntity(body: CreateSourceEntityBody): Promise<SourceEntity> {
  return apiFetch<SourceEntity>('/api/v1/sources', {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

/**
 * Прив'язує сутність до довідника; `null` — відв'язує (`ФВ-8.11`).
 *
 * ⛔ Без прив'язки мапінг на поле довідника сервер не приймає
 * (`err.ECR-REQ-0422.entityFieldMapRegistryNotBound`).
 */
export function bindSourceEntityRegistry(id: number, registryDefId: number | null): Promise<SourceEntity> {
  return apiFetch<SourceEntity>(`/api/v1/sources/${id}/registry`, {
    method: 'PUT',
    body: JSON.stringify({ registryDefId }),
  });
}
