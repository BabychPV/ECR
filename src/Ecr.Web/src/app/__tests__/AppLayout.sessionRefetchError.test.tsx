import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { AppLayout } from '@/app/AppLayout';
import { MeQueryKey } from '@/shared/session/useSession';

/**
 * L9-05: невдалий ФОНОВИЙ перезапит `/me` (503 під час перезапуску служби,
 * обрив VPN) не викидає на `/login` з живого сеансу — сторінка лишається, над
 * нею банер. На вхід — лише коли профілю немає зовсім, і тоді повернення
 * передається в `?from=`, яке читає `LoginPage`.
 */
let meStatus = 200;

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function renderAt(path: string): { client: QueryClient; router: ReturnType<typeof createMemoryRouter> } {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      { path: '/', element: <AppLayout />, children: [{ path: 'admin/units', element: <div>Units page</div> }] },
      { path: '/login', element: <div>Login page</div> },
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

  return { client, router };
}

describe('AppLayout: збій перезапиту /me (L9-05)', () => {
  beforeEach(() => {
    meStatus = 200;
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes('/api/v1/me')) {
          if (meStatus !== 200) {
            return jsonResponse({ type: 'about:blank', title: 'Unavailable', status: meStatus }, meStatus);
          }
          return jsonResponse({
            userId: 1,
            userName: 'admin',
            language: 'en',
            permissions: ['Unit.View'],
            grants: {},
            denies: [],
            mustChangePassword: false,
            isSimulation: false,
            simulatedForUserId: null,
            simulatedForUserName: null,
            simulationSessionId: null,
          });
        }
        if (url.includes('/ui-strings/')) {
          return jsonResponse({ languageCode: 'en', revision: 1, strings: { 'err.http.unavailable': 'Server unreachable' } });
        }

        if (url.includes('/preferences')) return jsonResponse([]);

        return jsonResponse(null);
      }),
    );
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it('сторінка лишається, маршрут не змінюється, показано банер', async () => {
    const { client, router } = renderAt('/admin/units');
    expect(await screen.findByText('Units page')).toBeTruthy();

    meStatus = 503;
    await act(async () => {
      await client.refetchQueries({ queryKey: MeQueryKey });
    });

    await waitFor(() => expect(document.querySelector('[data-session-refetch-error]')).not.toBeNull());
    expect(router.state.location.pathname).toBe('/admin/units');
    expect(screen.queryByText('Login page')).toBeNull();
    expect(screen.getByText('Units page')).toBeTruthy();
  });

  it('профілю немає зовсім — на вхід, повернення у ?from=', async () => {
    meStatus = 503;
    const { router } = renderAt('/admin/units?tab=2');

    expect(await screen.findByText('Login page')).toBeTruthy();
    expect(router.state.location.pathname).toBe('/login');
    expect(new URLSearchParams(router.state.location.search).get('from')).toBe('/admin/units?tab=2');
  });
});
