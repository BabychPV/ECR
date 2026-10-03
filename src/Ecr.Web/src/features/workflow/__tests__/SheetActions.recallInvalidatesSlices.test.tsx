import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SheetActions } from '../SheetActions';

/**
 * AN-28 / L8-02: після Recall/Return/Reopen сітка лишалась сірою до F5.
 *
 * Причина: `refresh` інвалідував лише `['document', id, period]`, а
 * `cellPermissions` (EditRules.CanEdit -> DocumentSubmitted) лежать у зрізі
 * `['table-slice', tableInstanceId, period]` зі `staleTime` 5 хв і без
 * `refetchOnWindowFocus`. Перехід стану аркуша мусить інвалідувати зрізи ЦЬОГО
 * аркуша (і лише його - CL-02).
 */

const CurrentUser = {
  denies: [],
  grants: {},
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: [],
  simulatedForUserId: null,
  userId: 9,
  userName: 'author',
};

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) return json(CurrentUser);
      if (url.includes('/recall') && init?.method === 'POST') return new Response(null, { status: 204 });
      if (url.includes('/recall?')) return json({ canRecall: true });

      return json({});
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('AN-28 L8-02: Recall інвалідує зрізи аркуша', () => {
  it('після Recall зріз цього аркуша позначений застарілим', async () => {
    mockFetch();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    // Таблиця 7 - аркуш 42 (той, що відкликається); таблиця 8 - інший аркуш.
    client.setQueryData(['document-tables', 1, 202601], [
      { tableInstanceId: 7, sheetDefId: 42 },
      { tableInstanceId: 8, sheetDefId: 43 },
    ]);
    client.setQueryData(['table-slice', 7, 202601], { cellPermissions: {} });
    client.setQueryData(['table-slice', 8, 202601], { cellPermissions: {} });

    render(
      <MantineProvider>
        <QueryClientProvider client={client}>
          <SheetActions documentId={1} sheetDefId={42} periodKey={202601} state="Submitted" />
        </QueryClientProvider>
      </MantineProvider>,
    );

    fireEvent.click(await screen.findByRole('button', { name: /recall\W?$/i }));
    fireEvent.change(await screen.findByRole('textbox'), { target: { value: 'wrong month' } });
    fireEvent.click(screen.getAllByRole('button', { name: /recall\W?$/i }).at(-1) as HTMLElement);

    await waitFor(() => {
      expect(client.getQueryState(['table-slice', 7, 202601])?.isInvalidated).toBe(true);
    });
  });
});
