import type { CurrentUserDto } from '@/api/types';
import { can } from '@/shared/session/useSession';
import type { RouteHandle } from './routes';

/**
 * Чи відкривається маршрут цьому користувачеві — ОДНЕ правило для навбару
 * (`AppLayout.tsx`) і гарда (`RouteGuard.tsx`).
 *
 * ⚠ Без `permission` маршрут доступний усім; інакше досить `permission` АБО
 * будь-якого з `alsoPermittedBy`. Два місця з власною копією цієї умови
 * розійшлися б рівно тоді, коли запис отримає друге право: пункт є в меню, а
 * перехід дає відмову (або навпаки).
 *
 * ⛔ `me === undefined` (профіль ще не завантажено) — `false` для будь-якого
 * маршруту з правом: `can()` зачинений за замовчуванням, і ця функція теж.
 */
export function canAccessRoute(me: CurrentUserDto | undefined, handle: RouteHandle): boolean {
  if (handle.permission === undefined) return true;

  return (
    can(me, handle.permission) || (handle.alsoPermittedBy ?? []).some((permission) => can(me, permission))
  );
}
