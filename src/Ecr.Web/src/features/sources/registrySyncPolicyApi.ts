import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/** Політика синку довідника з AF для сутності збору (`D-212`). */
export type RegistrySyncPolicy = components['schemas']['SetRegistrySyncPolicyRequest'];
export type RegistryMissingPolicy = components['schemas']['RegistryMissingPolicy'];
export type SourceEntityWithPolicy = components['schemas']['SourceEntityDto'];

/*
 * ⛔ Адреса записана повністю, а не збирається з помічника — той самий прийом,
 * що й `features/integration/sourceEntityApi.ts`: сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає літерал `/api/v1/…`
 * разом із методом поруч.
 *
 * Споживач — форма `RegistrySyncPolicyModal.tsx` з вкладки Entities шухляди
 * з'єднання (`SourceEntitiesTab.tsx`, D-212 PR-8).
 */

/**
 * Замінює політику синку довідника цілком (`PUT`, не латка).
 *
 * ⚠ Сутність не прив'язана до довідника — `422 ECR-REQ-0422`
 * (`registrySyncPolicyNotBound`); без права на дані довідника — `403`.
 */
export function setRegistrySyncPolicy(
  sourceEntityId: number,
  policy: RegistrySyncPolicy,
): Promise<SourceEntityWithPolicy> {
  return apiFetch<SourceEntityWithPolicy>(`/api/v1/sources/${sourceEntityId}/registry/policy`, {
    method: 'PUT',
    body: JSON.stringify(policy),
  });
}
