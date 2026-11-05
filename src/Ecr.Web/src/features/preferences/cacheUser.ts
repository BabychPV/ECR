import type { QueryClient } from '@tanstack/react-query';
import type { CurrentUserDto } from '@/api/types';
import { MeQueryKey } from '@/shared/session/useSession';

/**
 * Користувач, якому належить кеш першого рендера в `localStorage` (ширини й закріплені колонки сітки).
 *
 * ⛔ Ключ кешу без користувача протікав між людьми на спільному браузері: Б бачив вибір А, доки не прийде
 * `GET /me/preferences` (P3, координатор 05.11). Тому ключ несе id з `/me`, а старі ключі без id не читаються
 * зовсім - ціна лише одна: наявний користувач один раз бачить типове значення до відповіді сервера.
 *
 * @returns `null` - профіль ще невідомий: кеш тоді не читається й не пишеться (діє сервер / типове).
 */
export function cacheUserId(client: QueryClient): number | null {
  const id = client.getQueryData<CurrentUserDto>(MeQueryKey)?.userId;

  return typeof id === 'number' && Number.isFinite(id) ? id : null;
}
