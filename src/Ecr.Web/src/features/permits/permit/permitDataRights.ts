import { can, useSession } from '@/shared/session/useSession';

/**
 * Право вести дозволи: заводити, змінювати шапку, рядки таблиць і керувати життєвим циклом
 * (сервер: `Registry.EditData` або ресурсний грант на реєстр шапки типу — `PermitsController`).
 *
 * ⚠ Читання дозволів — `Registry.View` (право маршруту). Ресурсний грант клієнт не бачить: людина з одним лише
 * грантом без `Registry.EditData` бачить екран лише для читання, хоча сервер їй запис дозволив би. Це
 * обережна сторона: кнопка, яка не з'явилась, краща за кнопку, що дасть 403.
 */
export const PermitDataPermission = 'Registry.EditData';

/** Чи може ПОТОЧНИЙ користувач змінювати дозволи: має право й сеанс не є симуляцією (симуляція лише читає). */
export function useMayEditPermits(): boolean {
  const session = useSession();
  return can(session.data, PermitDataPermission) && session.data?.isSimulation !== true;
}
