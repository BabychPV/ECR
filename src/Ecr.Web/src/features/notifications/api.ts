import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/** Канал сповіщень. ⛔ Секрету тут немає й не буде — лише `hasSecret`. */
export type NotificationChannel = components['schemas']['NotificationChannelView'];
export type NotificationChannelSettings = components['schemas']['NotificationChannelSettings'];
export type CreateNotificationChannelBody = components['schemas']['CreateNotificationChannelRequest'];
export type UpdateNotificationChannelBody = components['schemas']['UpdateNotificationChannelRequest'];
/** `ok: false` — відповідь каналу, а не помилка запиту; `messageKey` — ключ каталогу, якщо причина відома. */
export type NotificationTestResult = components['schemas']['NotificationTestResult'];

/*
 * ⛔ Адреси записані повністю, а не збираються з помічника — той самий прийом,
 * що й `features/units/api.ts`: сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає літерал `/api/v1/…`
 * разом із методом поруч.
 *
 * ⚠ Екрана `/admin/notifications` (рішення 2.3 директиви №15) ще НЕМАЄ — модуль
 * поки має єдиного споживача, власний тест. Так сказано навмисно: коментар, що
 * обіцяє неіснуючий екран, дорожчий за відсутній.
 */

function json(body: unknown): RequestInit {
  return { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) };
}

export function listNotificationChannels(): Promise<NotificationChannel[]> {
  return apiFetch<NotificationChannel[]>('/api/v1/notifications/channels');
}

export function createNotificationChannel(body: CreateNotificationChannelBody): Promise<NotificationChannel> {
  return apiFetch<NotificationChannel>('/api/v1/notifications/channels', { method: 'POST', ...json(body) });
}

export function updateNotificationChannel(
  id: number,
  body: UpdateNotificationChannelBody,
): Promise<NotificationChannel> {
  return apiFetch<NotificationChannel>(`/api/v1/notifications/channels/${id}`, { method: 'PUT', ...json(body) });
}

/** Видаляє канал разом із його правилами; журнал доставок лишається. */
export function deleteNotificationChannel(id: number): Promise<void> {
  return apiFetch<void>(`/api/v1/notifications/channels/${id}`, { method: 'DELETE' });
}

/**
 * Замінює секрет (пароль SMTP або URL вебхука Teams); `null` — прибирає.
 * Недозволений хост вебхука — `422 ECR-REQ-0422` (`webhookUrlNotAllowed`).
 */
export function replaceNotificationChannelSecret(id: number, secret: string | null): Promise<NotificationChannel> {
  return apiFetch<NotificationChannel>(`/api/v1/notifications/channels/${id}/secret`, {
    method: 'PUT',
    ...json({ secret }),
  });
}

export function testNotificationChannel(id: number): Promise<NotificationTestResult> {
  return apiFetch<NotificationTestResult>(`/api/v1/notifications/channels/${id}/test`, { method: 'POST' });
}
