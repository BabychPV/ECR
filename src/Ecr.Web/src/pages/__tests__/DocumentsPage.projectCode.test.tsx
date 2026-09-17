import { describe, it, vi, afterEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * Аудит-пас 5: колонка «Project» показувала голий числовий `projectId` —
 * та сама сутність, чий код уже видно в діалозі «New document» одним
 * кліком поруч (`CreateDocumentModal`).
 */
const project = { id: 1, code: 'AUDIT_SMOKE_PRJ', status: 'Active' as const };

const document_ = {
  id: 1,
  businessKey: 'P1-V1-0001',
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 1,
  sheetStates: {},
};

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return new Response(
          JSON.stringify({
            denies: [],
            grants: {},
            isSimulation: false,
            language: 'en',
            mustChangePassword: false,
            permissions: [],
            simulatedForUserId: null,
            userId: 1,
            userName: 'tester',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/api/v1/documents')) {
        return new Response(
          JSON.stringify({ items: [document_], nextCursor: null, totalCount: 1 }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/api/v1/projects')) {
        return new Response(JSON.stringify({ items: [project], nextCursor: null, totalCount: 1 }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(null), { status: 200 });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/']}>
        <QueryClientProvider client={client}>
          <DocumentsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('DocumentsPage: аудит-пас 5', () => {
  it(
    'показує КОД проєкту, а не голий числовий id',
    async () => {
      mockFetch();
      show();

      const table = await screen.findByRole('table', {}, { timeout: SlowEnvTimeout });
      await within(table).findByText('AUDIT_SMOKE_PRJ', {}, { timeout: SlowEnvTimeout });
    },
    SlowEnvTimeout,
  );
});
