import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createMemoryRouter, RouterProvider, type RouteObject } from 'react-router-dom';
import { loadCatalog } from '@/shared/i18n';
import { ForbiddenPage } from '@/app/ForbiddenPage';
import { router } from '@/app/router';
import { theme } from '@/shared/theme/theme';
import { RouteHeadingClass } from '@/shared/theme/routeHeading';

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
    'err.ECR-AUTH-0403.requiresPermission': 'Requires permission',
    'nav.accessDenied.title': 'You don’t have access to this page',
    'nav.accessDenied.text': 'Nothing was changed. Ask a system administrator for a role that includes this permission.',
    'nav.accessDenied.copy': 'Copy request details',
    'nav.accessDenied.myAccess': 'See my access',
    'nav.backToDocuments': 'Back to Documents',
    'permission.Security.ManageRoles': 'Manage roles',
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
    expect(alert.textContent).toContain('You don’t have access to this page');
    expect(alert.textContent).toContain('Requires permission');
    // b4b, макет `/403`: право — людською назвою, код — окремим чипом поруч.
    expect(alert.textContent).toContain('«Manage roles»');
    expect(alert.querySelector('[data-forbidden-permission]')?.textContent).toBe('Security.ManageRoles');
    expect(alert.textContent).toContain('ECR-AUTH-0403');
    expect(screen.getByRole('button', { name: 'Copy request details' })).toBeTruthy();

    expect(screen.getByRole('link', { name: '← Back to Documents' }).getAttribute('href')).toBe('/');
    expect(screen.getByRole('link', { name: 'See my access' }).getAttribute('href')).toBe('/my-groups');
  });

  // ⛔ Мутаційний доказ завдання: «причина (яке право) не передається/не
  // показується → червоне». Це і є той стан — перехід НАПРЯМУ на `/403`
  // (закладка, вручну набраний URL), без `state` від `RouteGuard` — сторінка
  // МАЄ лишитись явною (заголовок, посилання додому), а не впасти чи
  // показати код права, якого не отримала.
  it('право в state відсутнє (прямий перехід на /403) — заголовок і посилання є, рядка з кодом права немає', async () => {
    await renderResolved(undefined);

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('You don’t have access to this page');
    expect(alert.textContent).not.toContain('Requires permission');
    expect(alert.querySelector('[data-forbidden-permission]')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Copy request details' })).toBeNull();

    expect(screen.getByRole('link', { name: '← Back to Documents' })).toBeTruthy();
  });

  it('фокус переходить на заголовок відмови (клавіатурний прохід, ФВ-14.19)', async () => {
    await renderResolved({ permission: 'Security.ManageRoles' });

    expect(document.activeElement?.textContent).toBe('You don’t have access to this page');
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

/**
 * ✎ 2026-10-06, вимога людини: «треба прибрати рамку, у текст в одну строку».
 *
 * ⚠ jsdom не верстає й не рахує `:focus-visible`, тому тут — рішення розмітки:
 * заголовок несе клас без рамки (`motion.css`, `motion.test.tsx`) і розмір
 * із гібридного макета (`.work-state-h` 15px = `md`), а контейнер — ширину
 * `.work-state` 560px замість 420px, у яких 26px заголовок ламався надвоє.
 * Справжні рядки й `outline` у Chromium — `e2e/errorPageHeading.spec.ts`.
 */
describe('ForbiddenPage: заголовок без рамки, в один рядок', () => {
  it('заголовок фокусований, без зупинки Tab, з класом без рамки і розміром макета', async () => {
    await renderResolved({ permission: 'Security.ManageRoles' });

    const heading = screen.getByRole('heading', { name: 'You don’t have access to this page' });

    expect(document.activeElement).toBe(heading);
    expect(heading.getAttribute('tabindex')).toBe('-1');
    expect(heading.classList.contains(RouteHeadingClass)).toBe(true);
    expect(heading.style.fontSize).toBe('var(--mantine-font-size-md)');
  });

  it('контейнер відмови — 560px макета, не 420px', async () => {
    await renderResolved({ permission: 'Security.ManageRoles' });

    expect(screen.getByRole('alert').style.maxWidth).toBe('calc(35rem * var(--mantine-scale))');
  });
});
