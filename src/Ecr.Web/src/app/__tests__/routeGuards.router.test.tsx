import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, Outlet, RouterProvider } from 'react-router-dom';
import type { CurrentUserDto } from '@/api/types';
import { ForbiddenPage } from '@/app/ForbiddenPage';
import { RouteGuard } from '@/app/RouteGuard';
import { routes } from '@/app/routes';
import { MeQueryKey } from '@/shared/session/useSession';
import { theme } from '@/shared/theme/theme';

/**
 * Рольові гарди — інтеграція зі СПРАВЖНІМ роутером (`PR nav-arch #4`,
 * `Q-279`).
 *
 * ⚠ Дерево нижче навмисно НЕ імпортує `router` із `router.tsx` (той — уже
 * `createBrowserRouter`, розрахований на реальний `window.history`, не на
 * `initialEntries` тестів) — це той самий підхід, що й
 * `Breadcrumbs.test.tsx` (`PR #3`): власне мінімальне дерево
 * `createMemoryRouter`, зібране з ТИХ САМИХ записів реєстру
 * (`routes.adminTemplates.handle`, і т. д.), обгорнутих `<RouteGuard>` так
 * само, як `guarded()` робить у `router.tsx`. Це перевіряє саме те, що
 * директива (A3) вимагає тестом: «користувач без ролі не потрапляє; з
 * роллю — потрапляє» — на СПРАВЖНЬОМУ маршрутизаторі (навігація за адресою,
 * не прямий рендер компонента), для трьох різних прав, не одного.
 *
 * ✎ **`UI-09`, L-правило про доступ.** Дерево несе ТАКОЖ `/403` зі
 * справжньою `ForbiddenPage` (не маркер-заглушку, на відміну від
 * `RouteGuard.test.tsx`, де перевіряється лише факт навігації): саме тут —
 * доказ, якого вимагає завдання картки: «перехід на захищений маршрут без
 * права → редирект на `/403`, сторінка показує причину» — на СПРАВЖНЬОМУ
 * маршрутизаторі, не ізольовано.
 */

function meWith(permissions: string[]): CurrentUserDto {
  return {
    userId: 1,
    userName: 'tester',
    language: 'en',
    permissions,
    denies: [],
    grants: {},
    isSimulation: false,
    simulatedForUserId: null,
    mustChangePassword: false,
  };
}

function Layout(): JSX.Element {
  return <Outlet />;
}

/** Три захищені сторінки під `/admin/*` — три РІЗНІ права з реєстру `routes.ts`. */
function buildRouter(initialPath: string): ReturnType<typeof createMemoryRouter> {
  return createMemoryRouter(
    [
      {
        path: '/',
        element: <Layout />,
        children: [
          {
            path: 'admin',
            element: <Layout />,
            children: [
              {
                path: 'templates',
                element: (
                  <RouteGuard handle={routes.adminTemplates.handle}>
                    <div data-testid="templates-content">Templates page</div>
                  </RouteGuard>
                ),
                handle: routes.adminTemplates.handle,
              },
              {
                path: 'security',
                element: (
                  <RouteGuard handle={routes.adminSecurity.handle}>
                    <div data-testid="security-content">Security page</div>
                  </RouteGuard>
                ),
                handle: routes.adminSecurity.handle,
              },
              {
                path: 'audit',
                element: (
                  <RouteGuard handle={routes.adminAudit.handle}>
                    <div data-testid="audit-content">Audit page</div>
                  </RouteGuard>
                ),
                handle: routes.adminAudit.handle,
              },
            ],
          },
          { path: '403', element: <ForbiddenPage /> },
        ],
      },
    ],
    { initialEntries: [initialPath] },
  );
}

function renderAt(
  path: string,
  me: CurrentUserDto,
): { view: ReturnType<typeof render>; router: ReturnType<typeof buildRouter> } {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(MeQueryKey, me);
  const router = buildRouter(path);

  const view = render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return { view, router };
}

const cases = [
  // ✎ `X-38`: перелік шаблонів читається з `Template.View` — як на сервері.
  { path: '/admin/templates', testId: 'templates-content', permission: 'Template.View' },
  { path: '/admin/security', testId: 'security-content', permission: 'Security.ManageRoles' },
  { path: '/admin/audit', testId: 'audit-content', permission: 'Security.ViewAudit' },
] as const;

describe.each(cases)('$path — право $permission', ({ path, testId, permission }) => {
  beforeEach(() => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(null, { status: 200, headers: { 'Content-Type': 'application/json' } })),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('користувач З правом переходить на маршрут і бачить сторінку', () => {
    const { view, router } = renderAt(path, meWith([permission]));

    expect(view.getByTestId(testId).textContent?.length).toBeGreaterThan(0);
    expect(view.queryByRole('alert')).toBeNull();
    expect(router.state.location.pathname).toBe(path);
  });

  it('користувач БЕЗ права редиректиться на /403 і бачить явну відмову з причиною — не порожній екран, не мовчазний редирект', () => {
    const { view, router } = renderAt(path, meWith([]));

    expect(view.queryByTestId(testId)).toBeNull();

    // ⚠ UI-09: раніше відмова рендерилась INLINE на адресі `path`; тепер це
    // СПРАВЖНЯ навігація на `/403` (`RouteGuard.tsx`: `<Navigate .../>`) —
    // перевіряється й адреса, і те, що причина (право) пережила перехід
    // через `state`, а не згубилась на ньому.
    expect(router.state.location.pathname).toBe('/403');

    const denial = view.getByRole('alert');
    expect(denial.textContent).not.toBe('');
    expect(denial.textContent).toContain(permission);
  });

  it('користувач з ІНШИМ правом (не тим, що вимагає маршрут) так само редиректиться на /403', () => {
    const { view, router } = renderAt(path, meWith(['Unrelated.Permission']));

    expect(view.queryByTestId(testId)).toBeNull();
    expect(router.state.location.pathname).toBe('/403');
    expect(view.getByRole('alert')).toBeTruthy();
  });
});
