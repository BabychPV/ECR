import { useEffect, useRef, type JSX } from 'react';
import { Anchor, Center, Stack, Text, Title } from '@mantine/core';
import { Link } from 'react-router-dom';
import { t } from '@/shared/i18n';
import { announceRoute } from '@/shared/ui/RouteAnnouncer';
import { RouteHeadingClass } from '@/shared/theme/routeHeading';

/**
 * Каталог маршрутів застосунку не покриває кожну адресу (`router.tsx` не
 * має жодного `path: '*'`/`errorElement`) — без цього компонента невідома
 * адреса під `/` (застаріле посилання, помилка в URL) показувала б голий
 * дефолтний екран React Router («Unexpected Application Error! 404 Not
 * Found... Hey developer 👋») — розробницький текст, не розрахований на
 * реального користувача, знайдено живим переходом на неіснуючу адресу.
 *
 * ⚠ Той самий прийом фокуса й `aria-live`, що й `AccessDeniedPage`
 * (`RouteGuard.tsx`, `Q-279`): це друга сторінка застосунку, змонтована
 * всередині `AppLayout`, що не веде власного `PageHeader` — та сама причина,
 * розмітка відмови центрована, а не `Group justify="space-between"`.
 */
export function NotFoundPage(): JSX.Element {
  const heading = useRef<HTMLHeadingElement>(null);
  const focused = useRef(false);
  const title = t('nav.notFound.title');

  useEffect(() => {
    if (focused.current) return;

    focused.current = true;
    heading.current?.focus();
  }, []);

  useEffect(() => {
    announceRoute(title);
  }, [title]);

  return (
    <Center py="xl">
      {/* ✎ 2026-10-06, вимога людини «текст в одну строку»: заголовок відмови
          ламався на два рядки — 26px (`order={2}`) у контейнері 420px. Тепер
          за гібридним макетом (`docs/design/hybrid`, `.work-state` 560px,
          `.work-state-h` 15px/600): рядок уміщається на будь-якій звичайній
          ширині, а на вузькому екрані `ta="center"` дає перенос по центру. */}
      <Stack gap="xs" align="center" maw={560} role="alert">
        <Title order={2} fz="md" fw={600} ref={heading} tabIndex={-1} ta="center" className={RouteHeadingClass}>
          {title}
        </Title>

        <Text size="sm" c="dimmed" ta="center">
          {t('nav.notFound.hint')}
        </Text>

        <Anchor component={Link} to="/" size="sm">
          {t('nav.documents')}
        </Anchor>
      </Stack>
    </Center>
  );
}
