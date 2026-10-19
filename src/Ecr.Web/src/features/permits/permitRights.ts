import { can, useSession } from '@/shared/session/useSession';

/**
 * Право на керування типами пермітів (маршрути `/admin/permits/types[/:code]`, смуга розділів, кнопка «New type»).
 *
 * Типами керують права `Permit.EditType` / `Permit.PublishType` (сідить сервер, PM-04). Решта
 * (routes.ts, PermitsSubnav, PermitTypesPage, редактор типу) читає цю константу.
 */
export const PermitTypesPermission = 'Permit.EditType';

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
 * Право ПУБЛІКУВАТИ тип (сервер: `Permit.PublishType`), як і {@link PermitTypesPermission}.
 */
export const PermitTypesPublishPermission = 'Permit.PublishType';

/** Чи може ПОТОЧНИЙ користувач публікувати типи: має {@link PermitTypesPublishPermission} і сеанс не є симуляцією. */
export function useMayPublishPermitTypes(): boolean {
  const session = useSession();
  return can(session.data, PermitTypesPublishPermission) && session.data?.isSimulation !== true;
}
