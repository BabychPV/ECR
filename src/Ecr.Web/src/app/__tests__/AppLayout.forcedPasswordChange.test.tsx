import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { AppLayout } from '@/app/AppLayout';

/**
 * T1-15 (б): під примусовою зміною пароля (`mustChangePassword`) усі API відповідають 428,
 * тож пункти бічного меню — мертві посилання. Їх не має бути в дереві зовсім (не лише
 * візуально згорнутими: інакше вони лишаються в порядку Tab).
 */
function stubFetch(mustChangePassword: boolean): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const body = url.includes('/api/v1/me')
        ? {
            userId: 1,
            userName: 'tester',
            language: 'en',
            permissions: [],
            isSimulation: false,
            mustChangePassword,
          }
        : url.includes('/ui-strings/')
          ? { languageCode: 'en', revision: 1, strings: {} }
          : null;
      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

function renderApp(path: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: <AppLayout />,
        children: [
          { index: true, element: <div>Головна</div>, handle: { labelKey: 'home.label' } },
          {
            path: 'change-password',
            element: <div>Зміна пароля</div>,
            handle: { labelKey: 'password.title' },
          },
        ],
      },
    ],
    { initialEntries: [path] },
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

describe('AppLayout: меню під примусовою зміною пароля (T1-15 б)', () => {
  it('контроль: без прапорця пункти меню є', async () => {
    stubFetch(false);
    renderApp('/');

    await screen.findByText('Головна');
    expect(document.querySelectorAll('nav a, [data-navbar] a, .mantine-AppShell-navbar a').length).toBeGreaterThan(0);
  });

  it('з mustChangePassword у бічному меню немає жодного посилання, а гамбургера немає', async () => {
    stubFetch(true);
    renderApp('/change-password');

    await screen.findByText('Зміна пароля');
    expect(document.querySelectorAll('.mantine-AppShell-navbar a').length).toBe(0);
    expect(screen.queryByRole('button', { name: /menu/i })).toBeNull();
  });
});
