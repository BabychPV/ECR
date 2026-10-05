import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, Outlet, RouterProvider } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { AppLayout } from '@/app/AppLayout';
import { childPath, relativePath, routes } from '@/app/routes';

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

/** Шпигун секції/листа адмінки: монтування = сторінка пішла б запитами. */
function AdminSpy({ name }: { name: string }): JSX.Element {
  mounted(name);
  return (
    <div data-testid={`admin-${name}`}>
      {name}
      <Outlet />
    </div>
  );
}

/**
 * Та сама форма вкладеності, що в `router.tsx`: секція `admin/templates/:id`
 * з `handle` реєстру й індексним листом БЕЗ `handle` (картка шаблону).
 */
function showAdmin(path: string): void {
  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: <AppLayout />,
        children: [
          {
            path: 'admin',
            children: [
              {
                path: 'templates/:id',
                element: <AdminSpy name="template-section" />,
                handle: routes.adminTemplateSection.handle,
                children: [
                  { index: true, element: <AdminSpy name="template-card" /> },
                  {
                    path: relativePath(routes.adminTemplateVersion, 'admin/templates/:id'),
                    element: <AdminSpy name="template-version" />,
                    handle: routes.adminTemplateVersion.handle,
                  },
                  {
                    path: relativePath(routes.adminTemplateVersionRelations, 'admin/templates/:id'),
                    element: <AdminSpy name="template-relations" />,
                    handle: routes.adminTemplateVersionRelations.handle,
                  },
                ],
              },
              {
                path: relativePath(routes.adminMethodologyVersions, 'admin'),
                element: <AdminSpy name="methodology-versions" />,
                handle: routes.adminMethodologyVersions.handle,
              },
            ],
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

/**
 * L9-14: механізм `R-19`/`X-09` стояв лише на документі — `/admin/templates/abc`,
 * `/admin/templates/1/versions/x`, `/admin/methodologies/x/versions` монтували
 * сторінку, і та йшла запитами на `…/NaN`.
 */
describe('AppLayout: нечисловий ідентифікатор у адресах адмінки', () => {
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

  function requestedUrls(): string[] {
    return vi.mocked(fetch).mock.calls.map(([input]) => String(input));
  }

  it('/admin/templates/abc показує NotFound і не робить fetch', async () => {
    showAdmin('/admin/templates/abc');

    // ⛔ Мутація «прибрати `numericParams` з `routes.adminTemplateSection`» або
    // «перевіряти лише лист» (індексна картка без `handle`) монтує картку — червоний.
    expect(await screen.findByRole('heading', { name: '⟦nav.notFound.title⟧' })).toBeTruthy();
    expect(mounted).not.toHaveBeenCalled();
    expect(requestedUrls().filter((u) => u.includes('/templates') || u.includes('NaN'))).toEqual([]);
  });

  it.each([
    '/admin/templates/1/versions/x',
    '/admin/templates/abc/versions/2',
    '/admin/templates/1/versions/x/relations',
    '/admin/methodologies/x/versions',
  ])('%s — теж неіснуюча сторінка', async (path) => {
    showAdmin(path);

    expect(await screen.findByRole('heading', { name: '⟦nav.notFound.title⟧' })).toBeTruthy();
    expect(mounted).not.toHaveBeenCalled();
  });

  it.each([
    ['/admin/templates/7', 'template-card'],
    ['/admin/templates/7/versions/3', 'template-version'],
    ['/admin/templates/7/versions/3/relations', 'template-relations'],
    ['/admin/methodologies/4/versions', 'methodology-versions'],
  ])('%s — сторінка монтується як завжди', async (path, testId) => {
    showAdmin(path);

    expect(await screen.findByTestId(`admin-${testId}`)).toBeTruthy();
    expect(screen.queryByRole('heading', { name: '⟦nav.notFound.title⟧' })).toBeNull();
  });
});
