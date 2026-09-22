import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createMemoryRouter, RouterProvider, type RouteObject } from 'react-router-dom';
import { loadCatalog } from '@/shared/i18n';
import { ForbiddenPage } from '@/app/ForbiddenPage';
import { router } from '@/app/router';
import { theme } from '@/shared/theme/theme';

/**
 * Маршрут `/403` (`UI-09`, L-правило про доступ) — сторінка призначення
 * `<Navigate to="/403" .../>` з `RouteGuard.tsx`.
 *
 * ⛔ До цієї картки відмова рендерилась INLINE, усередині `RouteGuard.tsx`
 * (`AccessDeniedPage`), і не мала власного маршруту — `DIRECTIVE-15-
 * FRONTEND.md:193` прямо називала `/403` відсутнім (`◐`). Той самий доказ,
 * що вимагає завдання картки: тест мав бути ЧЕРВОНИМ до зміни — до неї
 * `ForbiddenPage.tsx`/`app/ForbiddenPage.tsx` не існував узагалі, тож будь-
 * який імпорт із цього файлу падав на "Cannot find module".
 *
 * ⚠ Структура файлу — той самий поділ, що вже задає `NotFoundPage.test.tsx`
 * для `/404`: рендер компонента ізольовано (реальний каталог рядків через
 * `loadCatalog`) + окрема статична перевірка, що `router.tsx` СПРАВДІ несе
 * `path: '403'` серед дітей кореня (не лише ізольоване тестове дерево).
 */
function stubCatalog(strings: Record<string, string>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(JSON.stringify({ languageCode: 'en', revision: 1, strings }), {
          status: 200,
          headers: { 'Content-Type': 'application/json', ETag: '"private-en-1"' },
        }),
      ),
    ),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

async function renderResolved(state: { permission?: string } | undefined): Promise<void> {
  stubCatalog({
    'err.ECR-AUTH-0403': 'You do not have permission for this action.',
    'err.ECR-AUTH-0403.requiresPermission': 'Requires permission',
    'nav.documents': 'Documents',
  });

  await act(async () => {
    await loadCatalog('en', 'private');
  });

  render(
    <MantineProvider theme={theme}>
      <RouterProvider
        router={createMemoryRouter([{ path: '/403', element: <ForbiddenPage /> }], {
          initialEntries: [{ pathname: '/403', state }],
        })}
      />
    </MantineProvider>,
  );
}

describe('ForbiddenPage', () => {
  it('право передано через state — показує заголовок, причину з кодом права і посилання на головну', async () => {
    await renderResolved({ permission: 'Security.ManageRoles' });

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('You do not have permission for this action.');
    expect(alert.textContent).toContain('Requires permission');
    expect(alert.textContent).toContain('Security.ManageRoles');

    const link = screen.getByRole('link', { name: 'Documents' });
    expect(link.getAttribute('href')).toBe('/');
  });

  // ⛔ Мутаційний доказ завдання: «причина (яке право) не передається/не
  // показується → червоне». Це і є той стан — перехід НАПРЯМУ на `/403`
  // (закладка, вручну набраний URL), без `state` від `RouteGuard` — сторінка
  // МАЄ лишитись явною (заголовок, посилання додому), а не впасти чи
  // показати код права, якого не отримала.
  it('право в state відсутнє (прямий перехід на /403) — заголовок і посилання є, рядка з кодом права немає', async () => {
    await renderResolved(undefined);

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('You do not have permission for this action.');
    expect(alert.textContent).not.toContain('Requires permission');

    expect(screen.getByRole('link', { name: 'Documents' })).toBeTruthy();
  });

  it('фокус переходить на заголовок відмови (клавіатурний прохід, ФВ-14.19)', async () => {
    await renderResolved({ permission: 'Security.ManageRoles' });

    expect(document.activeElement?.textContent).toBe('You do not have permission for this action.');
  });
});

/**
 * ⚠ Реальне дерево `router.tsx` (не переспрощена копія) — той самий прийом,
 * що вже підтверджує `path: '*'` для `/404` (`NotFoundPage.test.tsx`,
 * `Q-302`): статична перевірка форми `router.routes`, а не рендер
 * (`createBrowserRouter` розрахований на `window.history`).
 */
describe('router — каталог маршрутів застосунку', () => {
  it('корінь "/" несе дочірній маршрут "403" (UI-09)', () => {
    const root = router.routes.find((route) => route.path === '/');
    expect(root).toBeDefined();

    const descendants = (routes: RouteObject[]): RouteObject[] =>
      routes.flatMap((route) => [route, ...descendants(route.children ?? [])]);

    const forbiddenRoute = descendants(root?.children ?? []).find((route) => route.path === '403');
    expect(forbiddenRoute, 'router.tsx має нести path: "403" серед дітей кореня "/"').toBeDefined();
  });
});
