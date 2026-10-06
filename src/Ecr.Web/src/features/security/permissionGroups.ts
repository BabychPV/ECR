import type { PermissionCatalogItem, RoleView } from '@/api/types';
import { hasText, t } from '@/shared/i18n';

/**
 * Права, згруповані за доменом для розгортних груп ролі (UI-37, макет
 * `screens-ops.js` `paintDetail`: «Documents · 4 of 9 granted · 1 dangerous»).
 *
 * ⚠ Група — поле `PermissionCatalogItem.group` з `GET /permissions`, а не
 * префікс коду, розібраний тут: сервер уже каже, куди право належить, і друге
 * джерело групування розійшлося б із першим на першому ж праві з іншим
 * префіксом.
 */
export interface PermissionGroup {
  readonly group: string;
  readonly items: readonly PermissionCatalogItem[];
}

/** Групи в порядку першої появи в каталозі (каталог сторінка вже сортує за кодом). */
export function groupPermissions(catalog: readonly PermissionCatalogItem[]): PermissionGroup[] {
  const byGroup = new Map<string, PermissionCatalogItem[]>();

  for (const item of catalog) {
    const list = byGroup.get(item.group);
    if (list === undefined) byGroup.set(item.group, [item]);
    else list.push(item);
  }

  return [...byGroup.entries()].map(([group, items]) => ({ group, items }));
}

/**
 * Чи має роль право.
 *
 * ⚠ Об'єднання двох полів, а не лише `permissions`: `RoleView` віддає
 * небезпечні права ще й окремим переліком (ФВ-6.12, `D-40`), і роль, у якої
 * сервер назвав право лише там, не мала б показуватися як «не надано».
 */
export function roleHas(role: RoleView, code: string): boolean {
  return role.permissions.includes(code) || role.dangerousPermissions.includes(code);
}

/** Ключ каталогу під назву домену прав. */
export function permissionGroupKey(group: string): string {
  return `security.domain.${group}`;
}

/**
 * Назва домену мовою інтерфейсу; немає рядка — сам код групи.
 *
 * ⛔ Не `⟦security.domain.X⟧`: група приходить із сервера, і нова група
 * з'явиться раніше за рядок у `09-seed.sql` — той самий вибір, що й у
 * `permissionLabel.ts`.
 */
export function permissionGroupLabel(group: string): string {
  const key = permissionGroupKey(group);

  return hasText(key) ? t(key) : group;
}
