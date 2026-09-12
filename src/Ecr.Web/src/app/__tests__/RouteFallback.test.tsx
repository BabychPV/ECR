import { createElement, lazy, type ComponentType } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { AppLayout } from '@/app/AppLayout';
import type { RouteHandle } from '@/app/routes';

/**
 * Скелет `<Suspense>` за формою маршруту (`PR nav-arch #6`, розділи B3/C1
 * директиви) — наскрізна перевірка через СПРАВЖНІЙ `AppLayout`, той самий
 * прийом, що й `AppLayout.prefetch.test.tsx` (`PR nav-arch #5`): дочірній
 * маршрут монтується `lazy()`-завантажувачем, що НІКОЛИ не вирішується
 * (`new Promise(() => {})`) — `<Suspense fallback={<RouteFallback/>}>`
 * лишається у стані очікування назавжди, і саме це дає змогу оглянути сам
 * скелет, а не сторінку під ним.
 */
const MeResponse = {
  userId: 1,
  userName: 'tester',
  language: 'en',
  permissions: [],
  isSimulation: false,
  mustChangePassword: false,
};

function stubFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/v1/me')) {
        return new Response(JSON.stringify(MeResponse), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      if (url.includes('/ui-strings/')) {
        return new Response(JSON.stringify({ languageCode: 'en', revision: 1, strings: {} }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      return new Response(JSON.stringify(null), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

/**
 * Ніколи не вирішується: `<Suspense>` лишається на fallback навіки.
 *
 * ⚠ Застосунок підключає сторінки через `React.lazy()` в `element:`
 * (`router.tsx`/`routePrefetch.ts`), НЕ через нативне поле `lazy:` React
 * Router 7 (те очікує форму `{ Component, loader, ... }`, інше API) — тест
 * відтворює САМЕ той шлях, яким `<Suspense fallback={<RouteFallback/>}>`
 * реально спрацьовує в застосунку.
 */
function neverResolves(): Promise<{ default: ComponentType }> {
  return new Promise(() => {
    // Навмисно порожньо: цей `import()` не має вирішитись за час тесту.
  });
}

function renderAt(path: string, handle: RouteHandle): ReturnType<typeof render> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: <AppLayout />,
        children: [{ path, element: createElement(lazy(neverResolves)), handle }],
      },
    ],
    { initialEntries: [`/${path}`] },
  );

  return render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('RouteFallback: скелет за формою маршруту (PR nav-arch #6, директива B3/C1)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('handle.skeletonShape "table" (напр. home/myGroups/adminSecurity) показує табличний скелет', async () => {
    stubFetch();
    renderAt('table-route', { labelKey: 'x', skeletonShape: 'table' });

    expect(await screen.findByTestId('route-skeleton-table')).toBeDefined();
    expect(screen.queryByTestId('route-skeleton-form')).toBeNull();
    expect(screen.queryByTestId('route-skeleton-dashboard')).toBeNull();
    expect(screen.queryByTestId('route-skeleton-generic')).toBeNull();
  });

  it('handle.skeletonShape "form" (adminTemplateVersion) показує скелет форми/деталі', async () => {
    stubFetch();
    renderAt('form-route', { labelKey: 'x', skeletonShape: 'form' });

    expect(await screen.findByTestId('route-skeleton-form')).toBeDefined();
    expect(screen.queryByTestId('route-skeleton-table')).toBeNull();
  });

  it('handle.skeletonShape "dashboard" (adminHealth) показує сітку карток, не таблицю', async () => {
    stubFetch();
    renderAt('dashboard-route', { labelKey: 'x', skeletonShape: 'dashboard' });

    expect(await screen.findByTestId('route-skeleton-dashboard')).toBeDefined();
    expect(screen.queryByTestId('route-skeleton-table')).toBeNull();
  });

  it('маршрут БЕЗ skeletonShape лишається на загальному скелеті — не регресія (Q-281)', async () => {
    stubFetch();
    renderAt('untouched-route', { labelKey: 'x' });

    expect(await screen.findByTestId('route-skeleton-generic')).toBeDefined();
    expect(screen.queryByTestId('route-skeleton-table')).toBeNull();
    expect(screen.queryByTestId('route-skeleton-form')).toBeNull();
    expect(screen.queryByTestId('route-skeleton-dashboard')).toBeNull();
  });
});
