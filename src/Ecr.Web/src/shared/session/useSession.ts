import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { CurrentUserDto } from '@/api/types';

/**
 * Профіль поточного користувача з ефективними правами.
 *
 * ⚠ Права беруться з `/me` і враховуються **до** показу кнопки: користувач не
 * має тиснути те, що все одно дасть 403. Це не заміна серверній перевірці —
 * та лишається єдиним рішенням; це відсутність кнопок, які не працюють.
 *
 * ⛔ Форма — **згенерований** тип, а не власний `interface`. До аудиту
 * (`A7-05`) тут стояло `displayName`, а сервер віддавав `userName`: у шапці
 * не показувалося нічого, і `tsc` був зелений.
 */
export type MeDto = CurrentUserDto;

/** Ключ запиту профілю. */
export const MeQueryKey = ['me'] as const;

/** Читає профіль поточного користувача. */
export function useSession() {
  return useQuery({
    queryKey: MeQueryKey,
    queryFn: () => apiFetch<CurrentUserDto>('/api/v1/me'),

    // ⚠ Профіль НЕ кешується надовго: зміна ролей робить сесію недійсною
    // негайно (SecurityStamp), і показувати кнопки за старим профілем
    // означало б обіцяти дію, яку сервер уже не виконає.
    staleTime: 0,
    retry: false,
  });
}

/** Чи має користувач право. */
export function can(me: CurrentUserDto | undefined, permission: string): boolean {
  return me?.permissions.includes(permission) ?? false;
}
