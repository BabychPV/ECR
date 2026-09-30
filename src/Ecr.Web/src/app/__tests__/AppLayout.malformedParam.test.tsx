import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { AppLayout } from '@/app/AppLayout';
import { childPath, routes } from '@/app/routes';

/**
 * `R-19`/`X-09`: `/documents/abc` показував «HTTP 404 · HTTP-404…», а в мережі
 * було три запити на `…/NaN` — сторінка документа монтувалася з `Number('abc')`.
 *
 * ⚠ Сторінку тут замінює шпигун із тим самим `handle`, що в `router.tsx`:
 * доводиться саме те, що сторінка НЕ монтується (а отже й не робить запитів),
 * а не лише те, що десь з'явився текст «не знайдено».
 */
const MeResponse = {
  userId: 1,
  userName: 'tester',
  language: 'en',
  permissions: [],
  isSimulation: false,
  mustChangePassword: false,
};

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

const mounted = vi.fn();

function DocumentSpy(): JSX.Element {
  mounted();
  return <div data-testid="document-page">document</div>;
}

function show(path: string): void {
  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: <AppLayout />,
        children: [
          {
            path: childPath(routes.documentDetail),
            element: <DocumentSpy />,
            handle: routes.documentDetail.handle,
          },
        ],
      },
    ],
    { initialEntries: [path] },
  );

  render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('AppLayout: нечисловий ідентифікатор у адресі документа', () => {
  beforeEach(() => {
    mounted.mockClear();
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);

        if (url.includes('/api/v1/me')) return jsonResponse(MeResponse);
        if (url.includes('/ui-strings/')) {
          return jsonResponse({ languageCode: 'en', revision: 1, strings: {} });
        }

        return jsonResponse(null);
      }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('/documents/abc — «сторінку не знайдено», сторінка документа не монтується', async () => {
    show('/documents/abc');

    // ⛔ Мутація «прибрати `numericParams` з `routes.documentDetail`» або
    // «рендерити `<Outlet/>` безумовно» монтує шпигуна — і тест червоний.
    expect(await screen.findByRole('heading', { name: '⟦nav.notFound.title⟧' })).toBeTruthy();
    expect(screen.queryByTestId('document-page')).toBeNull();
    expect(mounted).not.toHaveBeenCalled();
  });

  it.each(['/documents/12abc', '/documents/-1', '/documents/1.5'])('%s — теж не документ', async (path) => {
    show(path);

    expect(await screen.findByRole('heading', { name: '⟦nav.notFound.title⟧' })).toBeTruthy();
    expect(mounted).not.toHaveBeenCalled();
  });

  it('/documents/12 — сторінка документа монтується як завжди', async () => {
    show('/documents/12');

    expect(await screen.findByTestId('document-page')).toBeTruthy();
    expect(screen.queryByRole('heading', { name: '⟦nav.notFound.title⟧' })).toBeNull();
  });
});
