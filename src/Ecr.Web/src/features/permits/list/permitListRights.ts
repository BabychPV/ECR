import { can, useSession } from '@/shared/session/useSession';

/**
 * Право писати дозволи: створення, активація, дострокове завершення, поновлення, видалення чернетки.
 * Сервер (`PermitsController`) вимагає для всіх цих дій одне й те саме право `Registry.EditData`; читання
 * переліку — `Registry.View` (його має кожен, хто бачить розділ).
 */
export const PermitWritePermission = 'Registry.EditData';

/** Чи є право писати дозволи в ІМЕНИ ЦЬОГО сеансу (мінус симуляція: вона лише читає). */
export function mayWritePermits(
  me: Parameters<typeof can>[0],
): boolean {
  return can(me, PermitWritePermission) && me?.isSimulation !== true;
}

/**
 * Чи може поточний користувач писати дозволи. Поки сеанс не завантажено — `false`: кнопки не з'являються
 * «на вірку», а потім не зникають. Недоступну дію ХОВАЄМО, а не вимикаємо.
 */
export function useMayWritePermits(): boolean {
  const session = useSession();

  return mayWritePermits(session.data);
}
