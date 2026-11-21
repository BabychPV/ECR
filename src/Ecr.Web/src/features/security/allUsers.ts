import { fetchAllPages, type CursorPage } from '@/api/fetchAllPages';
import type { UserView } from '@/api/types';

/**
 * УСІ користувачі (`GET /api/v1/users`, право `Security.ManageUsers`) — для імен авторів і вибору людини (PSS-19c).
 *
 * ⛔ Три екрани читали по одній сторінці різного розміру під трьома різними ключами (`['users']` з `limit=200`,
 * `['users', 500]`, `['users', 'audit-authors']` з `limit=200`): 201-й користувач не мав імені в історії довідника
 * й у фільтрі журналу, а скільки показано, ніхто не казав. Тепер — один ключ і всі сторінки (`fetchAllPages`).
 *
 * ⚠ Ключ — під префіксом `['users']`: `invalidateQueries(['users'])` після створення чи зміни користувача освіжає
 * і цей перелік, і сторінки нескінченного переліку `SecurityPage` (`['users', 'list']`) — інша форма, інший ключ.
 */
export const allUsersQuery = {
  queryKey: ['users', 'all'] as const,
  queryFn: ({ signal }: { signal?: AbortSignal }): Promise<CursorPage<UserView>> =>
    fetchAllPages<UserView>('/api/v1/users', { signal }),
} as const;
