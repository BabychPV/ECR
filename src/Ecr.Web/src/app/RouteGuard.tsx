import { useEffect, useRef, type JSX, type ReactNode } from 'react';
import { Anchor, Center, Code, Stack, Text, Title } from '@mantine/core';
import { Link } from 'react-router-dom';
import { can, useSession } from '@/shared/session/useSession';
import { t } from '@/shared/i18n';
import { announceRoute } from '@/shared/ui/RouteAnnouncer';
import type { RouteHandle } from './routes';

/**
 * Сторінка відмови в праві на рівні МАРШРУТУ (`PR nav-arch #4`, `Q-279`).
 *
 * ⚠ Той самий текст, що й серверна відмова `ECR-AUTH-0403`
 * (`ExceptionHandlingMiddleware.LocalizedDetailAsync`, `Q-242`): каталог уже
 * несе `err.ECR-AUTH-0403` (заголовок) і
 * `err.ECR-AUTH-0403.requiresPermission` (підпис перед кодом права) трьома
 * мовами інтерфейсу (`D-95`: en/ru/kz) — заводити другий, майже такий самий
 * рядок тут означало б розбіжність між тим, що каже сервер на 403, і тим, що
 * каже клієнт, коли сам не пускає на маршрут за тим самим правом. Обидва
 * ключі вже сеідовані (`09-seed.sql`, `Q-242`) — ця картка НІЧОГО не додає в
 * каталог рядків (картка не торкається DDL/seed за межею завдання).
 *
 * ⛔ Не редирект. Директива (A3) дає вибір «явна сторінка "немає доступу" АБО
 * редирект» — редирект на `/` за замовчуванням увів би в оману, ніби
 * забороненого маршруту не існує взагалі, тоді як ця сторінка називає
 * причину прямо (яке саме право потрібне) й лишає адресу в рядку — той самий
 * принцип, що вже діє для порожніх станів (`H-21`, `MyGroupsPage.tsx`):
 * пояснення краще за мовчазну відсутність.
 *
 * ⚠ Фокус на заголовку й оголошення `aria-live` (`PR nav-arch #7`) — той
 * самий прийом, що й `PageHeader.tsx` (`Q-261`, `ФВ-14.19`), навмисно
 * ІНЛАЙН тут, а не через сам компонент `PageHeader`: розмітка відмови
 * центрована (`Center`/`Stack align="center"`), а `PageHeader` несе власну
 * розкладку (`Group justify="space-between"`, дії праворуч) — підміняти
 * layout заради самої лише поведінки означало б непов'язану візуальну
 * регресію поза межами цієї картки. Це ЄДИНА сторінка застосунку, змонтована
 * всередині `AppLayout`, що досі обходила `PageHeader` (перевірено грепом
 * перед цією карткою: усі 24 листові маршрути `routes.ts` уже використовують
 * `PageHeader`) — без цього фіксу перехід на заборонений маршрут лишав би
 * користувача клавіатури на старому пункті навбару й без жодного оголошення,
 * чому екран щойно змінився.
 */
export function AccessDeniedPage({ permission }: { permission: string }): JSX.Element {
  const heading = useRef<HTMLHeadingElement>(null);
  const focused = useRef(false);
  const title = t('err.ECR-AUTH-0403');

  useEffect(() => {
    // ⛔ Рівно ОДИН раз за монтування — той самий інваріант, що й
    // `PageHeader.tsx`: сторінка відмови не отримує оновлень заголовка після
    // монтування, тож ref-прапорець тут суто про послідовність із джерелом
    // прийому, не про захист від реального повторного виклику.
    if (focused.current) return;

    focused.current = true;
    heading.current?.focus();
  }, []);

  useEffect(() => {
    announceRoute(title);
  }, [title]);

  return (
    <Center py="xl">
      <Stack gap="xs" align="center" maw={420} role="alert">
        <Title order={2} ref={heading} tabIndex={-1}>
          {title}
        </Title>

        <Text size="sm" c="dimmed" ta="center">
          {t('err.ECR-AUTH-0403.requiresPermission')} <Code>{permission}</Code>
        </Text>

        {/* ⛔ Не тупиковий екран (`ФВ-14.24`): посилання назад на домашній
            маршрут — єдиний доступний хід, який не вимагає жодного права. */}
        <Anchor component={Link} to="/" size="sm">
          {t('nav.documents')}
        </Anchor>
      </Stack>
    </Center>
  );
}

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
 */
export function RouteGuard({
  handle,
  children,
}: {
  handle: RouteHandle;
  children: ReactNode;
}): JSX.Element {
  const session = useSession();

  if (handle.permission !== undefined && !can(session.data, handle.permission)) {
    return <AccessDeniedPage permission={handle.permission} />;
  }

  return <>{children}</>;
}
