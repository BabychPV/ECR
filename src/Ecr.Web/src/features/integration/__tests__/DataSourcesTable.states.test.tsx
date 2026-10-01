import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { DataSourcesTable } from '@/features/integration/DataSourcesTable';
import { testTheme } from '@/test/render';

/**
 * Перелік з'єднань (`/admin/sources`): стани «помилка», «немає права»,
 * «порожньо», «завантаження» (`ФВ-14.22`) — через `DataTable`/`AsyncBoundary`.
 */
configure({ asyncUtilTimeout: 10_000 });

function serve(sources: () => Promise<Response>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input), 'http://localhost').pathname;

      if (path === '/api/v1/data-sources') return sources();

      if (path === '/api/v1/me') {
        return new Response(
          JSON.stringify({
            denies: [],
            grants: {},
            isSimulation: false,
            language: 'en',
            mustChangePassword: false,
            permissions: ['Integration.View', 'Integration.Manage'],
            simulatedForUserId: null,
            userId: 1,
            userName: 'me',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      return new Response('null', { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

const problem = (status: number, errorCode: string): Promise<Response> =>
  Promise.resolve(
    new Response(
      JSON.stringify({
        title: status === 403 ? 'Forbidden' : 'Server error',
        status,
        errorCode,
        correlationId: 'corr-data-sources',
        detail: null,
      }),
      { status, headers: { 'Content-Type': 'application/problem+json' } },
    ),
  );

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={['/admin/sources']}>
          <DataSourcesTable />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DataSourcesTable — стани', () => {
  it('500: показує помилку з кодом, а не «з\'єднань немає»', async () => {
    serve(() => problem(500, 'ECR-SYS-0500'));
    show();

    expect(await screen.findByText('ECR-SYS-0500', { exact: false })).toBeTruthy();
    expect(screen.queryByText('⟦sources.connectionsEmpty⟧')).toBeNull();
  });

  it('403: стан «немає права», а не «з\'єднань немає»', async () => {
    serve(() => problem(403, 'ECR-AUTH-0403'));
    show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-AUTH-0403');
    expect(screen.queryByText('⟦sources.connectionsEmpty⟧')).toBeNull();
  });

  it('порожній перелік: «з\'єднань немає» з поясненням і жодної таблиці', async () => {
    serve(() => Promise.resolve(new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } })));
    show();

    expect(await screen.findByText('⟦sources.connectionsEmpty⟧')).toBeTruthy();
    expect(screen.queryByText('⟦sources.connectionsEmptyHint⟧')).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('у дорозі: «завантаження», а не «з\'єднань немає»', async () => {
    serve(() => new Promise<Response>(() => undefined));
    show();

    expect((await screen.findByRole('status')).getAttribute('aria-busy')).toBe('true');
    expect(screen.queryByText('⟦sources.connectionsEmpty⟧')).toBeNull();
  });
});
