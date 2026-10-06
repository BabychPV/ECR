import type { CurrentUserDto } from '@/api/types';
import { can } from '@/shared/session/useSession';
import type { RouteHandle } from './routes';

/**
 * Чи відкривається маршрут цьому користувачеві — ОДНЕ правило для навбару
 * (`AppLayout.tsx`) і гарда (`RouteGuard.tsx`).
 *
 * ⚠ Без `permission` маршрут доступний усім; інакше потрібне `permission` АБО
 * будь-яке з `alsoPermittedBy`, І кожне з `alsoRequires`. Два місця з власною
 * копією цієї умови розійшлися б рівно тоді, коли запис отримає друге право: пункт є в меню, а
 * перехід дає відмову (або навпаки).
 *
 * ⛔ `me === undefined` (профіль ще не завантажено) — `false` для будь-якого
 * маршруту з правом: `can()` зачинений за замовчуванням, і ця функція теж.
 */
export function canAccessRoute(me: CurrentUserDto | undefined, handle: RouteHandle): boolean {
  return missingRoutePermission(me, handle) === undefined;
}

/**
 * Право, якого бракує для маршруту (`undefined` — маршрут відкритий). Його
 * називає відмова (`RouteGuard` → `ForbiddenPage`): основне `permission`, якщо
 * немає ні його, ні альтернатив, інакше перше відсутнє з `alsoRequires`.
 */
export function missingRoutePermission(
  me: CurrentUserDto | undefined,
  handle: RouteHandle,
): string | undefined {
  if (handle.permission === undefined) return undefined;

  const primary =
    can(me, handle.permission) || (handle.alsoPermittedBy ?? []).some((permission) => can(me, permission));
  if (!primary) return handle.permission;

  return (handle.alsoRequires ?? []).find((permission) => !can(me, permission));
}
