import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { AppLayout } from '@/app/AppLayout';

/**
 * V-06 (UX-прохід, третій раунд): під сеансом симуляції шапка показує, ЧИЇМИ
 * очима дивиться людина, і дає завершити сеанс — з номером сеансу з `/me`, а
 * не лише з `sessionStorage` вкладки, де сеанс почали.
 *
 * ⚠ Сервер тепер віддає `isSimulation`, ім'я суб'єкта й номер сеансу
 * (`SimulationSessionApiTests`); цей файл тримає клієнтський бік того самого.
 */
const Strings: Record<string, string> = {
  'app.simulating': 'Viewing as {user}',
  'security.simulationEnd': 'Stop',
  'security.simulationEnded': 'Back to your own permissions.',
};

let simulating = true;
const calls: { url: string; method: string }[] = [];

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(status === 204 ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function renderAppLayout(): void {
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

describe('AppLayout: банер симуляції (V-06)', () => {
  beforeEach(() => {
    simulating = true;
    calls.length = 0;
    globalThis.sessionStorage?.clear();

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        const method = init?.method ?? 'GET';
        calls.push({ url, method });

        if (url.includes('/api/v1/security/simulation') && method === 'DELETE') {
          simulating = false;
          return jsonResponse(null, 204);
        }
        if (url.includes('/api/v1/me')) {
          return jsonResponse({
            userId: 1,
            userName: 'admin',
            language: 'en',
            permissions: ['Document.View'],
            grants: {},
            denies: [],
            mustChangePassword: false,
            isSimulation: simulating,
            simulatedForUserId: simulating ? 17 : null,
            simulatedForUserName: simulating ? 'Aigerim Operator' : null,
            simulationSessionId: simulating ? 42 : null,
          });
        }
        if (url.includes('/ui-strings/')) {
          return jsonResponse({ languageCode: 'en', revision: 1, strings: Strings });
        }

        return jsonResponse(null);
      }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('показує ім\'я суб\'єкта і завершує сеанс номером із /me, без sessionStorage', async () => {
    renderAppLayout();

    expect(await screen.findByText('Viewing as Aigerim Operator')).toBeTruthy();

    // ⛔ У `sessionStorage` номера немає (сеанс почали в іншій вкладці) — кнопка
    // однаково є, бо номер прийшов із `/me`.
    await userEvent.setup().click(await screen.findByRole('button', { name: 'Stop' }));

    await waitFor(() =>
      expect(calls.some((c) => c.method === 'DELETE' && c.url.includes('sessionId=42'))).toBe(true),
    );
    await waitFor(() => expect(screen.queryByText(/Viewing as/)).toBeNull());
  });
});
