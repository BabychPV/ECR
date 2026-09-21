import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

// ⚠ Прямо зі схеми: `api/types.ts` — спільний файл поза межами цієї підзадачі.
type ResetPasswordRequest = components['schemas']['ResetPasswordRequest'];
type UserLockRequest = components['schemas']['UserLockRequest'];

/** Межа причини блокування/розблокування — та сама, що на сервері (`UserLockRequest`). */
export const LockReasonMaxLength = 400;

/**
 * Адміністрування облікового запису (`BE-12`): три дії, усі `204` без тіла.
 *
 * ⛔ Пароль іде ЛИШЕ тілом `POST`: не в адресі (журнали проксі й історія
 * браузера), не в ключі запиту (кеш `react-query`). Відповідь його не
 * повертає — сервер його не генерує.
 */
export function resetUserPassword(userId: number, newPassword: string): Promise<void> {
  return apiFetch<void>(`/api/v1/users/${userId}/reset-password`, {
    method: 'POST',
    body: JSON.stringify({ newPassword } satisfies ResetPasswordRequest),
  });
}

export function lockUser(userId: number, reason: string): Promise<void> {
  return apiFetch<void>(`/api/v1/users/${userId}/lock`, {
    method: 'POST',
    body: JSON.stringify({ reason } satisfies UserLockRequest),
  });
}

export function unlockUser(userId: number, reason: string): Promise<void> {
  return apiFetch<void>(`/api/v1/users/${userId}/unlock`, {
    method: 'POST',
    body: JSON.stringify({ reason } satisfies UserLockRequest),
  });
}
