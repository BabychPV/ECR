import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/** Зв'язок запису довідника з елементом зовнішнього джерела (`ФВ-8.10`). */
export type RegistryExternalKey = components['schemas']['RegistryExternalKeyView'];
type RegistryExternalKeyPage = components['schemas']['PagedResultOfRegistryExternalKeyView'];
type BindRegistryExternalKeyBody = components['schemas']['BindRegistryExternalKeyCommand'];

/*
 * ⛔ Адреси записані повністю, а не збираються з помічника — той самий прийом,
 * що й у `api.ts` довідників: сторож `Кожна_дія_сервера_має_споживача_в_інтерфейсі`
 * шукає літерал `/api/v1/…` разом із методом поруч.
 */

/**
 * Ключ кешу зв'язків запису — під префіксом `entries(code)` довідника, як і
 * `registryEntryKey`: зміна записів довідника інвалідує й ці зв'язки.
 */
export function externalKeysKey(
  code: string,
  entryId: number,
): readonly ['registries', 'entries', string, 'externalKeys', number] {
  return ['registries', 'entries', code, 'externalKeys', entryId] as const;
}

/**
 * Зв'язки ОДНОГО запису. ⚠ Сторінка одна (200): зв'язків у запису — одиниці
 * (по одному на джерело); гортання з'явиться разом із панеллю подій (S10).
 */
export function listExternalKeys(code: string, entryId: number): Promise<RegistryExternalKeyPage> {
  return apiFetch<RegistryExternalKeyPage>(
    `/api/v1/registries/${encodeURIComponent(code)}/external-keys?entryId=${String(entryId)}&limit=200`,
  );
}

/**
 * Прив'язує запис до елемента джерела.
 *
 * ⚠ Пара «джерело + ідентифікатор» уже прив'язана — `409 ECR-REG-0409`
 * (`externalKeyTaken`, називає запис, що її тримає).
 */
export function bindExternalKey(code: string, body: BindRegistryExternalKeyBody): Promise<RegistryExternalKey> {
  return apiFetch<RegistryExternalKey>(`/api/v1/registries/${encodeURIComponent(code)}/external-keys`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/** Відв'язує. Зв'язок запису іншого довідника — `404`. */
export function unbindExternalKey(code: string, id: number): Promise<void> {
  return apiFetch<void>(`/api/v1/registries/${encodeURIComponent(code)}/external-keys/${String(id)}`, {
    method: 'DELETE',
  });
}
