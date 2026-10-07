import type { JSX } from 'react';
import { Anchor, Button, Code, Group, Text } from '@mantine/core';
import { Link, useLocation } from 'react-router-dom';
import type { CurrentUserDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { useSession } from '@/shared/session/useSession';
import { canAccessRoute } from './routeAccess';
import { routeList, type RouteEntry } from './routes';
import { ServiceStatePage } from './ServiceStatePage';

/**
 * Схожі екрани для «Did you mean …?» (b4b; макет `screens-work.js` `/404`:
 * перші 5 літер останнього сегмента адреси шукаються в шляхах екранів).
 *
 * ⛔ Лише екрани з меню (`showInNav`), без параметрів у шляху і лише ДОСТУПНІ
 * цьому користувачу: підказка на екран, який одразу дасть 403, — гірша за
 * відсутність підказки.
 */
export function nearScreens(pathname: string, me: CurrentUserDto | undefined): RouteEntry[] {
  const last = pathname.split('/').filter(Boolean).pop()?.toLowerCase().slice(0, 5) ?? '';
  if (last.length === 0 || me === undefined) return [];

  return routeList
    .filter(
      (route) =>
        route.showInNav === true &&
        !route.path.includes(':') &&
        route.path.toLowerCase().includes(last) &&
        canAccessRoute(me, route.handle),
    )
    .slice(0, 3);
}

/** Відкриває палітру пошуку тією ж клавішею, що й людина (`SearchLauncher` слухає `window`). */
function openPalette(): void {
  window.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', code: 'KeyK', ctrlKey: true, bubbles: true }));
}

/**
 * Невідома адреса під `/` (`router.tsx`, `path: '*'`): без цього компонента
 * React Router показував розробницький екран «Unexpected Application Error!».
 *
 * Вигляд — за макетом (b4b; `screens-work.js` `/404`, D15-01): піктограма,
 * заголовок, пояснення, адреса моноширинним, «Did you mean …?», пошук
 * (Ctrl K) і головна «Go to Documents».
 */
export function NotFoundPage(): JSX.Element {
  const location = useLocation();
  const session = useSession();
  const near = nearScreens(location.pathname, session.data);

  return (
    <ServiceStatePage icon="route" title={t('nav.notFound.title')}>
      <Text size="sm" c="dimmed" ta="center">
        {t('nav.notFound.hint')}
      </Text>

      <Code data-not-found-path="">{location.pathname}</Code>

      {near.length > 0 && (
        <Text size="sm" ta="center" data-not-found-near="">
          {t('nav.notFound.didYouMean')}{' '}
          {near.map((route, index) => (
            <span key={route.id}>
              {index > 0 && ', '}
              <Anchor component={Link} to={route.path} size="sm">
                {t(route.handle.labelKey)}
              </Anchor>
            </span>
          ))}
        </Text>
      )}

      <Group gap="xs" justify="center" mt="xs">
        <Button variant="default" onClick={openPalette} rightSection={
            // ⚠ Не `Kbd`: його стилі відсічені (`mantineCssPrune.ts`) — той самий прийом, що `SearchLauncher`.
            <Text span size="xs" ff="monospace" aria-hidden="true">
              Ctrl K
            </Text>
          }>
          {t('nav.notFound.search')}
        </Button>
        <Button component={Link} to="/">
          {t('nav.goToDocuments')}
        </Button>
      </Group>
    </ServiceStatePage>
  );
}
