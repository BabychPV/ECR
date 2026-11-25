import type { JSX } from 'react';
import { Container } from '@mantine/core';
import { useRouteError } from 'react-router-dom';
import { RenderErrorScreen } from './RenderErrorScreen';

/**
 * `errorElement` маршрутів ВЕРХНЬОГО рівня (`/`, `/login`, службові) — ✎ 25.11.
 *
 * **Факт.** Межа `RouteErrorPage` стоїть ПІД `AppLayout` і ловить лише помилки сторінок. Помилка самого каркаса
 * — найчастіше відмова лінивого шматка каркаса після оновлення (`IdleSignOut-<хеш>.js` на live 25.11) — йшла
 * вище, у корінь, де межі не було, і React Router малював свій розробницький екран
 * («Unexpected Application Error! … Hey developer 👋»). Тут каркаса вже немає (впав саме він), тож екран
 * помилки займає сторінку сам — у власному `main`, щоб читалка мала орієнтир.
 */
export function RootErrorPage(): JSX.Element {
  const error = useRouteError();

  return (
    <Container component="main" size="sm" py="xl" data-testid="root-error-page">
      <RenderErrorScreen error={error} />
    </Container>
  );
}
