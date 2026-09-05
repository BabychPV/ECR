import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';

/**
 * Профіль поточного користувача з ефективними правами.
 *
 * ⚠ Права беруться з `/me` і враховуються **до** показу кнопки: користувач не
 * має тиснути те, що все одно дасть 403. Це не заміна серверній перевірці —
 * та лишається єдиним рішенням; це відсутність кнопок, які не працюють.
 */
export interface MeDto {
  userId: number;
  displayName: string;
  language: string;
  /** Ефективні права: `Document.Export`, `Registry.EditData`, … */
  permissions: string[];
  /** Разовий пароль: доки не змінено, доступні лише зміна пароля і вихід (ФВ-6.18). */
  mustChangePassword: boolean;
  /** Сеанс симуляції прав іншого користувача (ФВ-6.16a). */
  simulation: { targetUserId: number; targetName: string } | null;
}

/** Ключ запиту профілю. */
export const MeQueryKey = ['me'] as const;

/** Читає профіль поточного користувача. */
export function useSession() {
  return useQuery({
    queryKey: MeQueryKey,
    queryFn: () => apiFetch<MeDto>('/api/v1/me'),

    // ⚠ Профіль НЕ кешується надовго: зміна ролей робить сесію недійсною
    // негайно (SecurityStamp), і показувати кнопки за старим профілем
    // означало б обіцяти дію, яку сервер уже не виконає.
    staleTime: 0,
    retry: false,
  });
}

/** Чи має користувач право. */
export function can(me: MeDto | undefined, permission: string): boolean {
  return me?.permissions.includes(permission) ?? false;
}
