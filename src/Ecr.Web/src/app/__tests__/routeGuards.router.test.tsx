import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, Outlet, RouterProvider } from 'react-router-dom';
import type { CurrentUserDto } from '@/api/types';
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
        ],
      },
    ],
    { initialEntries: [initialPath] },
  );
}

function renderAt(path: string, me: CurrentUserDto): ReturnType<typeof render> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(MeQueryKey, me);
  const router = buildRouter(path);

  return render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const cases = [
  { path: '/admin/templates', testId: 'templates-content', permission: 'Template.Edit' },
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
    renderAt(path, meWith([permission]));

    expect(screen.getByTestId(testId).textContent?.length).toBeGreaterThan(0);
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('користувач БЕЗ права бачить явну відмову замість сторінки — не порожній екран, не мовчазний редирект', () => {
    renderAt(path, meWith([]));

    expect(screen.queryByTestId(testId)).toBeNull();

    const denial = screen.getByRole('alert');
    expect(denial.textContent).not.toBe('');
    expect(denial.textContent).toContain(permission);
  });

  it('користувач з ІНШИМ правом (не тим, що вимагає маршрут) так само бачить відмову', () => {
    renderAt(path, meWith(['Unrelated.Permission']));

    expect(screen.queryByTestId(testId)).toBeNull();
    expect(screen.getByRole('alert')).toBeTruthy();
  });
});
