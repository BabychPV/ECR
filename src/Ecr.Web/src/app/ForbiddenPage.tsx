import { useEffect, useRef, type JSX } from 'react';
import { Anchor, Center, Code, Stack, Text, Title } from '@mantine/core';
import { Link, useLocation } from 'react-router-dom';
import { t } from '@/shared/i18n';
import { announceRoute } from '@/shared/ui/RouteAnnouncer';
import { RouteHeadingClass } from '@/shared/theme/routeHeading';

/**
 * Стан локації, яким `RouteGuard` передає причину відмови (`UI-09`,
 * L-правило про доступ: окремий маршрут `/403` замість inline-відмови).
 *
 * ⚠ `state`, а не query-рядок (`?permission=...`). Той самий інваріант, що
 * вже діє для редиректу на `/login` (`AppLayout.tsx`:
 * `<Navigate to="/login" state={{ from: location.pathname }} />`): право —
 * не значення, яким користувач має ділитися посиланням чи бачити в
 * адресному рядку як параметр, яким нібито можна керувати.
 */
export interface ForbiddenLocationState {
  /** Право, якого бракує (`RouteHandle.permission`). */
  permission?: string;
}

/**
 * Окрема сторінка маршруту `/403` (`UI-09`, L-правило; `router.tsx`).
 *
 * ⛔ До цієї картки `RouteGuard` рендерив цю відмову INLINE, на адресі
 * забороненого маршруту (`AccessDeniedPage` жила прямо в `RouteGuard.tsx`) —
 * директива (A3) залишала вибір «явна сторінка "немає доступу" АБО редирект»
 * відкритим, а `DIRECTIVE-15-FRONTEND.md:193` прямо називала `/403`
 * відсутнім маршрутом (`◐`). Ця картка закриває прогалину: `RouteGuard`
 * тепер робить `<Navigate to="/403" replace state={{ permission }} />`
 * (`RouteGuard.tsx`) замість монтування відмови на місці — той самий підхід,
 * що вже діє для `/login` (`AppLayout.tsx`). Перевага окремого маршруту над
 * inline: одна канонічна адреса помилки, симетрична `/404`
 * (`NotFoundPage.tsx`) — обидві поза `routes.ts` (без права, без пункту
 * навбару), обидві останнім записом `withRenderErrorBoundary` у `router.tsx`.
 *
 * ⚠ Той самий текст, що й серверна відмова `ECR-AUTH-0403`
 * (`ExceptionHandlingMiddleware.LocalizedDetailAsync`, `Q-242`): каталог уже
 * несе `err.ECR-AUTH-0403` і `err.ECR-AUTH-0403.requiresPermission` трьома
 * мовами (`09-seed.sql`) — ця картка НІЧОГО не додає в каталог рядків.
 *
 * ⚠ `permission` у `state` може бути відсутнім: користувач може перейти на
 * `/403` НАПРЯМУ (закладка, вручну набраний URL, кнопка "назад" після
 * повторної навігації) — `useLocation().state` тоді `null`. Рядок «яке право
 * потрібне» показується лише коли право відоме; сам заголовок відмови — завжди,
 * так само, як `/404` завжди показує заголовок незалежно від того, звідки на
 * нього прийшли.
 *
 * ⚠ Той самий прийом фокуса й `aria-live`, що й раніше в `AccessDeniedPage`
 * (`Q-279`, `PR nav-arch #7`) — навмисно ІНЛАЙН тут, а не через `PageHeader`:
 * розмітка відмови центрована (`Center`/`Stack align="center"`), та сама
 * причина, що й у `NotFoundPage.tsx`.
 */
export function ForbiddenPage(): JSX.Element {
  const location = useLocation();
  const state = location.state as ForbiddenLocationState | null;
  const permission = state?.permission;

  const heading = useRef<HTMLHeadingElement>(null);
  const focused = useRef(false);
  const title = t('err.ECR-AUTH-0403');

  useEffect(() => {
    // ⛔ Рівно ОДИН раз за монтування — той самий інваріант, що й
    // `NotFoundPage.tsx`/колишній `AccessDeniedPage.tsx`.
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
        <Title order={2} ref={heading} tabIndex={-1} className={RouteHeadingClass}>
          {title}
        </Title>

        {permission !== undefined && (
          <Text size="sm" c="dimmed" ta="center">
            {t('err.ECR-AUTH-0403.requiresPermission')} <Code>{permission}</Code>
          </Text>
        )}

        {/* ⛔ Не тупиковий екран (`ФВ-14.24`): посилання назад на домашній
            маршрут — єдиний доступний хід, який не вимагає жодного права. */}
        <Anchor component={Link} to="/" size="sm">
          {t('nav.documents')}
        </Anchor>
      </Stack>
    </Center>
  );
}
