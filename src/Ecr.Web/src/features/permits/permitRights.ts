import { can, useSession } from '@/shared/session/useSession';

/**
 * Право на керування типами пермітів (маршрути `/admin/permits/types[/:code]`, смуга розділів, кнопка «New type»).
 *
 * ⚠ ТИМЧАСОВО `Registry.EditDefinition`: Аудит вирішив, що типами керують нові права `Permit.EditType` /
 * `Permit.PublishType` (сідить PM-04). Після мержу PM-04 оркестратор міняє значення ТУТ — один рядок; решта
 * (routes.ts, PermitsSubnav, PermitTypesPage, редактор типу, тести) читає цю константу.
 */
export const PermitTypesPermission = 'Registry.EditDefinition';

/**
 * Чи може ПОТОЧНИЙ користувач змінювати типи: має право `PermitTypesPermission` і сеанс не є
 * симуляцією (симуляція лише читає). Поки сеанс не завантажено — `false`: кнопки правки не з'являються
 * «на вірку», а потім не зникають.
 */
export function useMayEditPermitTypes(): boolean {
  const session = useSession();
  return can(session.data, PermitTypesPermission) && session.data?.isSimulation !== true;
}

/**
 * Право ПУБЛІКУВАТИ тип (сервер: `Registry.Publish`). ⚠ Стане `Permit.PublishType` після PM-04 — один рядок,
 * як і {@link PermitTypesPermission}.
 */
export const PermitTypesPublishPermission = 'Registry.Publish';

/** Чи може ПОТОЧНИЙ користувач публікувати типи: має {@link PermitTypesPublishPermission} і сеанс не є симуляцією. */
export function useMayPublishPermitTypes(): boolean {
  const session = useSession();
  return can(session.data, PermitTypesPublishPermission) && session.data?.isSimulation !== true;
}
