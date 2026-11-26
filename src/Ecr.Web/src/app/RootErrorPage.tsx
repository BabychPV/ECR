import type { JSX } from 'react';
import { Box } from '@mantine/core';
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
 *
 * ⚠ `Box component="main"` із пропами відступів (ФВ-14.11: без інлайн-стилів), а не Mantine `Container`: стилі `Container` відсічені зі збірки
 * (`UnusedMantineComponents`, `mantineCssPrune.ts`, бюджет D-132) — тягнути їх заради одного аварійного екрана не варто.
 */
export function RootErrorPage(): JSX.Element {
  const error = useRouteError();

  return (
    <Box component="main" data-testid="root-error-page" maw={720} mx="auto" py="xl" px="md">
      <RenderErrorScreen error={error} />
    </Box>
  );
}
