import { lazy, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ProjectLayout } from '@/pages/projects/ProjectLayout';
import { testTheme } from '@/test/render';

/**
 * ✎ Projects 08.11 (огляд координатора, f6 — дефект продукту): після Clone адреса вже вказує на клон, а поки чанк
 * вкладки вантажиться, на екрані лишалась сторінка ДЖЕРЕЛА з вкладками на нього (Archive, Delete). Шапка й вкладки
 * мусять одразу показувати проєкт з адреси, вміст — скелет.
 *
 * ⛔ Доказ «падає без фіксу»: без `<Suspense key={pathname}>` у `ProjectLayout` перехід (startTransition React
 * Router) тримає старе дерево — вкладка Lifecycle веде на `/projects/6/lifecycle`, видно вміст проєкту 6.
 */

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

afterEach(() => {
  vi.unstubAllGlobals();
});

function Lifecycle(): JSX.Element {
  return <p data-testid="old-content">lifecycle of 6</p>;
}

// Вкладка, чий чанк ніколи не приїжджає (модель повільного лінивого чанка).
const NeverSettings = lazy(() => new Promise<{ default: () => JSX.Element }>(() => undefined));

describe('ProjectLayout: зміна проєкту в адресі', () => {
  it('вкладки одразу ведуть на проєкт з адреси, старий вміст зникає, поки вантажиться нова вкладка', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        if (/\/projects\/\d+\/periods/.test(url)) return json({ projectId: 6, periodKind: 'Monthly', timeZoneId: 'UTC', periods: [] });
        if (url.includes('/api/v1/projects')) {
          return json({
            items: [
              { id: 6, code: 'SRC', status: 'Active', periodKind: 'Monthly', periodCount: 12, currentPeriodId: null, timeZoneId: 'UTC' },
              { id: 7, code: 'CLONE', status: 'Draft', periodKind: 'Monthly', periodCount: 12, currentPeriodId: null, timeZoneId: 'UTC' },
            ],
            nextCursor: null,
            totalCount: 2,
          });
        }
        return json(null);
      }),
    );

    const router = createMemoryRouter(
      [
        {
          path: '/projects/:id',
          element: <ProjectLayout />,
          children: [
            { path: 'lifecycle', element: <Lifecycle /> },
            { path: 'settings', element: <NeverSettings /> },
          ],
        },
      ],
      { initialEntries: ['/projects/6/lifecycle'] },
    );

    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <RouterProvider router={router} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    expect(await screen.findByTestId('old-content')).toBeTruthy();

    await act(async () => {
      await router.navigate('/projects/7/settings');
    });

    await waitFor(() =>
      expect(document.querySelector('[data-project-tab="lifecycle"]')?.getAttribute('href')).toBe('/projects/7/lifecycle'),
    );
    expect(screen.queryByTestId('old-content')).toBeNull();
    expect(document.querySelector('[data-project-tab-loading]')).not.toBeNull();
    expect(screen.getByRole('heading', { level: 1 }).textContent).toContain('CLONE');
  });
});
