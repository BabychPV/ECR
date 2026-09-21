import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, Outlet, RouterProvider } from 'react-router-dom';
import type { CurrentUserDto } from '@/api/types';
import { AppLayout } from '@/app/AppLayout';
import { RouteGuard } from '@/app/RouteGuard';
import { routes } from '@/app/routes';
import { MeQueryKey } from '@/shared/session/useSession';
import { theme } from '@/shared/theme/theme';

/**
 * `/admin/sources` відкривається за `Integration.View` АБО
 * `Integration.Manage` — так само, як сервер пускає до
 * `GET /api/v1/data-sources` (`0e73e80e`).
 *
 * ⛔ Червоне на коді до зміни: маршрут вимагав лише `Integration.Manage`, тож
 * людина з `Integration.View` (роль `Auditor` у сіді) бачила відмову замість
 * з'єднань, які сервер їй віддає, і не мала пункту в меню.
 *
 * ⚠ Файл навмисно не імпортує `routeAccess.ts`: перевіряються два справжні
 * споживачі правила — гард і навбар, — а не сама функція.
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

/** Прямий перехід за адресою через справжній маршрутизатор і справжній гард. */
function visitSources(me: CurrentUserDto): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(MeQueryKey, me);

  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: <Layout />,
        children: [
          {
            path: 'admin/sources',
            element: (
              <RouteGuard handle={routes.adminSources.handle}>
                <div data-testid="sources-content">Sources page</div>
              </RouteGuard>
            ),
            handle: routes.adminSources.handle,
          },
        ],
      },
    ],
    { initialEntries: ['/admin/sources'] },
  );

  render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Навбар справжнього `AppLayout` із профілем, який віддає `/api/v1/me`. */
function renderNav(permissions: string[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const body = url.includes('/api/v1/me')
        ? meWith(permissions)
        : url.includes('/ui-strings/')
          ? { languageCode: 'en', revision: 1, strings: {} }
          : null;

      return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [{ path: '/', element: <AppLayout />, children: [{ index: true, element: <div>Main</div> }] }],
    { initialEntries: ['/'] },
  );

  render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Пункт навбару за адресою — не за підписом, який залежить від каталогу рядків. */
function navLink(path: string): Element | null {
  return document.querySelector(`.mantine-AppShell-navbar a[href="${path}"]`);
}

/** Навбар домалювався: домашній пункт (без права) є завжди. */
async function navReady(): Promise<void> {
  await waitFor(() => expect(navLink('/')).not.toBeNull());
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('/admin/sources — прямий перехід (RouteGuard)', () => {
  it('лише Integration.View — маршрут доступний', () => {
    visitSources(meWith(['Integration.View']));

    expect(screen.getByTestId('sources-content')).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('лише Integration.Manage — маршрут доступний', () => {
    visitSources(meWith(['Integration.Manage']));

    expect(screen.getByTestId('sources-content')).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('ні того, ні того — явна відмова з назвою права, як на інших маршрутах', () => {
    visitSources(meWith(['Template.Edit']));

    expect(screen.queryByTestId('sources-content')).toBeNull();
    // Відмова називає те саме право, що й серверний `403` цього переліку.
    expect(screen.getByRole('alert').textContent).toContain('Integration.View');
  });
});

describe('/admin/sources — пункт навбару (AppLayout)', () => {
  it('лише Integration.View — пункт є; «Mapping» (лише Manage) — немає', async () => {
    renderNav(['Integration.View']);
    await navReady();

    await waitFor(() => expect(navLink('/admin/sources')).not.toBeNull());
    expect(navLink('/admin/mapping')).toBeNull();
  });

  it('лише Integration.Manage — пункт є', async () => {
    renderNav(['Integration.Manage']);
    await navReady();

    await waitFor(() => expect(navLink('/admin/sources')).not.toBeNull());
    expect(navLink('/admin/mapping')).not.toBeNull();
  });

  it('ні того, ні того — пункту немає', async () => {
    renderNav(['Template.Edit']);
    await navReady();

    // Орієнтир, що фільтр уже відпрацював саме з цим профілем.
    await waitFor(() => expect(navLink('/admin/templates')).not.toBeNull());
    expect(navLink('/admin/sources')).toBeNull();
  });
});
