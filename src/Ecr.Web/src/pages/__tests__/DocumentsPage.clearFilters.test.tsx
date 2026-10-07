import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * `UI-18` (решта після лінії B): «Clear filters» у ряду фільтрів (макет
 * `FilterBar`, KIT §3) — з'являється сама за активного фільтра і скидає все
 * одним переходом; без фільтрів кнопки немає.
 */
const SlowEnvTimeout = 400_000;

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/api/v1/me')) {
        return json({ denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false, permissions: [], simulatedForUserId: null, userId: 1, userName: 'tester' });
      }
      if (url.includes('/api/v1/documents/summary')) return json({ draft: 1, submitted: 0, approved: 0, rejected: 0, withIssues: 0 });
      if (url.includes('/api/v1/documents')) {
        return json({
          items: [{ id: 1, businessKey: 'DOC-000001', createdAt: '2026-01-01T00:00:00Z', projectId: 1, sheetCount: 1, sheetStates: { S1: 'Draft' }, hasLateEdits: false }],
          nextCursor: null,
          totalCount: 1,
        });
      }
      if (url.includes('/api/v1/projects')) return json({ items: [{ id: 1, code: 'ATR', status: 'Active' }], nextCursor: null, totalCount: 1 });

      return json(null);
    }),
  );
}

function LocationProbe(): JSX.Element {
  const location = useLocation();

  return <span data-testid="location">{location.search}</span>;
}

function show(url: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[url]}>
        <QueryClientProvider client={client}>
          <DocumentsPage />
          <LocationProbe />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentsPage: «Clear filters» (UI-18)', () => {
  it(
    'без активних фільтрів кнопки немає',
    async () => {
      mockFetch();
      show('/?periodKey=202609');

      await screen.findByText('DOC-000001', {}, { timeout: SlowEnvTimeout });
      expect(screen.queryByRole('button', { name: '⟦documents.clearFilters⟧' })).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'активні фільтри — кнопка є і скидає ВСІ одним кліком, період лишається',
    async () => {
      mockFetch();
      show('/?periodKey=202609&state=Draft&mine=true&hasLateEdits=true');
      const user = userEvent.setup();

      const clear = await screen.findByRole('button', { name: '⟦documents.clearFilters⟧' }, { timeout: SlowEnvTimeout });
      await user.click(clear);

      await waitFor(() => expect(screen.getByTestId('location').textContent).toBe('?periodKey=202609'), {
        timeout: SlowEnvTimeout,
      });
      expect(screen.queryByRole('button', { name: '⟦documents.clearFilters⟧' })).toBeNull();
    },
    SlowEnvTimeout,
  );
});
