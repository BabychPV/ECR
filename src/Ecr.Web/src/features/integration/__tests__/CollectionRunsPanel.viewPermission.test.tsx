import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, Outlet, RouterProvider } from 'react-router-dom';
import type { CurrentUserDto } from '@/api/types';
import { ForbiddenPage } from '@/app/ForbiddenPage';
import { RouteGuard } from '@/app/RouteGuard';
import { routes } from '@/app/routes';
import { SourcesPage } from '@/pages/admin/SourcesPage';
import { MeQueryKey } from '@/shared/session/useSession';
import { theme } from '@/shared/theme/theme';

/**
 * Журнал прогонів (ФВ-5.23) на `/admin/sources` — доступ ДЗЕРКАЛИТЬ сам
 * маршрут: `Integration.View` АБО `Integration.Manage`
 * (`routes.ts:adminSources`, `ListCollectionRunsHandler.Permissions`).
 *
 * ⛔ Мутаційний доказ. `CollectionRunsPanel.tsx` НЕ ставить власної перевірки
 * права — секція відкрита кожному, хто вже пройшов гард маршруту. Якби хтось
 * додав туди `if (!can(session.data, 'Integration.Manage')) return null;`
 * (типова помилка «скопіювали дію з Manage-кнопки і забули, що весь екран
 * відкритий і за View»), перший тест нижче падає: заголовок журналу не
 * знаходиться попри те, що маршрут відкрився.
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

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function respond(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input), 'http://localhost').pathname;

      if (path === '/api/v1/collection-runs') return json({ items: [], nextCursor: null, totalCount: null });
      if (path === '/api/v1/data-sources') return json([]);
      if (path === '/api/v1/sources') return json([]);

      return json(null);
    }),
  );
}

function visitSources(me: CurrentUserDto): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(MeQueryKey, me);

  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: <Outlet />,
        children: [
          {
            path: 'admin/sources',
            element: (
              <RouteGuard handle={routes.adminSources.handle}>
                <SourcesPage />
              </RouteGuard>
            ),
            handle: routes.adminSources.handle,
          },
          // ⚠ UI-09: ціль `<Navigate to="/403" .../>` з `RouteGuard.tsx` —
          // без цього запису відмова без права не мала б куди навігувати.
          { path: '403', element: <ForbiddenPage /> },
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

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Журнал прогонів на /admin/sources — той самий доступ, що маршрут', () => {
  it('лише Integration.View — журнал видно (не заховано за Manage)', async () => {
    respond();
    visitSources(meWith(['Integration.View']));

    expect(await screen.findByText('⟦collectionRuns.title⟧')).toBeTruthy();
    expect(screen.queryByRole('alert', { name: /Integration/i })).toBeNull();
  });

  it('лише Integration.Manage — журнал так само видно', async () => {
    respond();
    visitSources(meWith(['Integration.Manage']));

    expect(await screen.findByText('⟦collectionRuns.title⟧')).toBeTruthy();
  });

  it('ні того, ні того — маршрут відмовляє РАНІШЕ, ніж журнал устигає щось запитати', async () => {
    respond();
    visitSources(meWith(['Template.Edit']));

    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(screen.queryByText('⟦collectionRuns.title⟧')).toBeNull();
  });
});
