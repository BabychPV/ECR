import { useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { abandonSwitchedSession, apiFetch, setSessionUserId } from '@/api/client';
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

/**
 * Користувач, якого ця вкладка вже бачила в `/me` (AN-108 / S2-05).
 *
 * ⛔ Другий рубіж після `sessionChannel`: якщо сповіщення не дійшло, а `/me` раптом відповідає ІНШИМ користувачем
 * (cookie підмінив вхід у сусідній вкладці), вкладка покидає сеанс так само — до того, як автозбереження
 * відправить утримані правки попереднього користувача під новим cookie. Легальна зміна користувача у вкладці
 * завжди йде через повне перезавантаження (вихід, `401` → вхід) — а з ним і новий кеш запитів, тож пам'ять
 * прив'язана до кешу (`QueryClient`), а не до модуля.
 */
const seenUserIds = new WeakMap<QueryClient, number>();

/** Звіряє користувача `/me` з тим, кого цей кеш уже бачив; повертає профіль без змін. */
export function checkSessionUser(client: QueryClient, me: CurrentUserDto): CurrentUserDto {
  const seen = seenUserIds.get(client);
  if (seen !== undefined && me.userId !== seen) {
    abandonSwitchedSession();
    return me;
  }
  seenUserIds.set(client, me.userId);
  // AN-108 / S2-05: небезпечні запити вкладки несуть цей id (`X-Ecr-User`) — сервер звірить його з cookie.
  setSessionUserId(me.userId);
  return me;
}

/** Скільки разів повторювати збійний `/me` (L9-05). */
export const MeMaxRetries = 1;

function isClientError(error: unknown): boolean {
  const status = (error as { problem?: { status?: number } } | null)?.problem?.status;
  return status !== undefined && status >= 400 && status < 500;
}

/** Читає профіль поточного користувача. */
export function useSession() {
  const client = useQueryClient();

  return useQuery({
    queryKey: MeQueryKey,
    queryFn: async () => checkSessionUser(client, await apiFetch<CurrentUserDto>('/api/v1/me')),

    // ⛔ P2-06: `refetchOnMount: true` (дефолт) при `staleTime: 0` давав `GET /me` (профіль прав = 1–2 SQL) на
    // КОЖНЕ монтування одного з ~50 споживачів `useSession()` — кожен перехід маршруту, кожне відкриття
    // шухляди чи діалогу. Миттєву недійсність сеансу після зміни ролей забезпечує сервер
    // (`SecurityStampMiddleware` → `401` на першому ж запиті), а кнопки лише підказка (коментар угорі), тож
    // за наявного профілю монтування не перезапитує. Без профілю (перше завантаження, відмова) запит іде.
    // Після довгої відсутності профіль перечитує `refetchOnWindowFocus`, а свідомі зміни ролей/налаштувань
    // інвалідують `MeQueryKey` явно (`SimulationPanel`, `ChangePasswordPage`).
    //
    // ⛔ НЕ `staleTime: 15_000`, хоч спершу так і було: скінченний `staleTime` на цьому запиті вішав
    // `SecurityPage.accessFocusReturn.test.tsx` у гейті `client` (воркер `vmThreads` мовчав 6–11 хв і падав
    // `Worker exited unexpectedly`, 5 прогонів поспіль; варіант `refetchOnMount: false` + `staleTime: 0` —
    // зелений, 854/854). Причину зависання не з'ясовано — див. звіт lane r11-l6.
    staleTime: 0,
    refetchOnMount: false,
    // ⛔ L9-05 / AN-97: один повтор для минущого збою (502 під час перезапуску служби, обрив
    // VPN) — без нього будь-який такий збій на першому завантаженні вів на `/login`.
    // 4xx не повторюється (як у `createQueryClient`): `403` повтор не виправить, а `401`
    // `apiFetch` уже обробив перенаправленням.
    retry: (failureCount, error) => !isClientError(error) && failureCount < MeMaxRetries,
  });
}

/** Чи має користувач право. */
export function can(me: CurrentUserDto | undefined, permission: string): boolean {
  return me?.permissions.includes(permission) ?? false;
}
