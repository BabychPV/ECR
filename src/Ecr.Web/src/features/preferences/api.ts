import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/**
 * Налаштування користувача на сервері (`BE-20`, `/api/v1/me/preferences`).
 *
 * ⛔ Адреси записані повністю, а не збираються з префікса: сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає літерал `/api/v1/…`
 * разом із методом поруч (той самий прийом, що й `features/jobs/api.ts`).
 *
 * ⚠ Тут немає `useMutation`: відмова запису налаштування не показується
 * користувачеві (його вибір уже застосовано локально), а `MutationCache.onError`
 * показав би тост на кожну мутацію без власного `onError`.
 */
export type UserPreference = components['schemas']['UserPreferenceDto'];

/** Усі збережені налаштування поточного користувача. */
export async function getPreferences(): Promise<UserPreference[]> {
  return apiFetch<UserPreference[]>('/api/v1/me/preferences');
}

/** Upsert одного налаштування; тіло — саме JSON-значення. */
export async function putPreference(key: string, value: unknown): Promise<UserPreference> {
  return apiFetch<UserPreference>(`/api/v1/me/preferences/${encodeURIComponent(key)}`, {
    method: 'PUT',
    body: JSON.stringify(value),
  });
}

/** Видаляє налаштування (204). */
export async function deletePreference(key: string): Promise<void> {
  await apiFetch<void>(`/api/v1/me/preferences/${encodeURIComponent(key)}`, { method: 'DELETE' });
}
