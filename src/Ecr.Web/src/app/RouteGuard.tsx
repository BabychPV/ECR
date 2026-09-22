import type { JSX, ReactNode } from 'react';
import { Navigate } from 'react-router-dom';
import { useSession } from '@/shared/session/useSession';
import { canAccessRoute } from './routeAccess';
import type { RouteHandle } from './routes';

/**
 * Гард маршруту (`PR nav-arch #4`) — компонентний, застосовується ОДНАКОВО
 * до кожного запису `routes.ts` через `guarded()` у `router.tsx`.
 *
 * ⚠ Обрано КОМПОНЕНТНИЙ підхід, не `loader()`+`redirect()`. Право
 * користувача (`CurrentUserDto.permissions`) уже живе в кеші TanStack Query
 * під ключем `useSession` (`['me']`) — тим самим, який навбар (`AppLayout`)
 * читає для приховування пунктів (`can(me, permission)`). `loader()` читав
 * би той самий кеш (`queryClient.ensureQueryData`), тобто дублював би одне й
 * те саме джерело правди ДВОМА способами звернення до нього замість одного,
 * і кожна майбутня зміна форми права (`Q-238`, `Q-239` — обидва про
 * розбіжність приватного/публічного шляху перевірки прав) означала б
 * синхронізувати два місця замість одного.
 *
 * ⛔ Не обгортає ЛИШЕ маршрути з `permission` — обгортає КОЖЕН запис
 * реєстру (`guarded()` викликається для всіх дітей `router.tsx`), а сам
 * гард пропускає рендер наскрізь, коли `handle.permission === undefined`.
 * Один шлях, а не два («маршрут із гардом» і «маршрут без гарда»), які
 * могли розійтися того дня, коли запис отримає право пізніше, а виклик
 * `guarded()` для нього забудуть додати.
 *
 * ⛔ `can()` вертає `false`, коли профіль (`session.data`) ще не завантажено
 * (`me === undefined`) — гард «зачинений за замовчуванням» на власному рівні
 * теж, а не лише покладається на те, що `AppLayout` блокує рендер `Outlet`
 * до резолву сесії (`AppLayout.tsx`: `session.isPending` → спінер,
 * `session.isError` → редирект на `/login`). Другий незалежний прошарок тієї
 * самої гарантії дешевший за дефект, який стається лише тоді, коли перший
 * колись зміниться.
 *
 * ✎ **`UI-09`, L-правило про доступ.** До цієї картки відмова рендерилась
 * INLINE, на адресі забороненого маршруту (`AccessDeniedPage` жила прямо
 * тут). Тепер — окремий маршрут: `<Navigate to="/403" replace state=
 * {{ permission }} />` (`ForbiddenPage.tsx`), той самий прийом, що вже діє
 * для `/login` (`AppLayout.tsx`: `<Navigate to="/login" state={{ from }}
 * />`). `replace`, а не звичайний перехід: кнопка «назад» із `/403` веде на
 * адресу ДО забороненого маршруту, а не знову на нього (той самий інваріант,
 * що й у переходу на `/login`) — без `replace` історія росла б на кожній
 * спробі відкрити недоступний маршрут.
 *
 * ⚠ Директива (A3) лишала вибір «явна сторінка "немає доступу" АБО редирект»
 * відкритим; `DIRECTIVE-15-FRONTEND.md:193` прямо називала `/403` відсутнім
 * маршрутом. Редирект на ОКРЕМУ явну сторінку поєднує обидва: адреса
 * називає причину не гірше за inline-варіант (`ForbiddenPage` і далі
 * показує, яке саме право потрібне, через `state`), а сама сторінка — ОДНА
 * на застосунок, симетрична `/404` (`NotFoundPage.tsx`), а не 20+ копій
 * одного й того самого дерева розмітки, змонтованих під кожним забороненим
 * листом реєстру.
 */
export function RouteGuard({
  handle,
  children,
}: {
  handle: RouteHandle;
  children: ReactNode;
}): JSX.Element {
  const session = useSession();

  if (handle.permission !== undefined && !canAccessRoute(session.data, handle)) {
    return <Navigate to="/403" replace state={{ permission: handle.permission }} />;
  }

  return <>{children}</>;
}
