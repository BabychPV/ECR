import type { CurrentUserDto } from '@/api/types';
import { canAccessRoute } from './routeAccess';
import { navGroups, type NavGroup, type RouteEntry } from './routes';

/** Група меню з пунктами, дозволеними користувачеві. */
export interface VisibleNavGroup extends NavGroup {
  readonly items: RouteEntry[];
}

/**
 * Групи меню (UI-12) для цього користувача: пункти — за `canAccessRoute`,
 * група без жодного дозволеного пункту зникає.
 *
 * ⚠ ОДНЕ правило для меню (`AppLayout`) і командної палітри (UI-30): екран,
 * якого немає в меню, палітра не пропонує, і навпаки.
 */
export function visibleNavGroups(me: CurrentUserDto): VisibleNavGroup[] {
  // T1-15 (б): під примусовою зміною пароля пунктів немає зовсім.
  if (me.mustChangePassword) return [];

  return navGroups
    .map((group) => ({ ...group, items: group.routes.filter((route) => canAccessRoute(me, route.handle)) }))
    .filter((group) => group.items.length > 0);
}
