import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ConsistencyIssuesPage } from '@/pages/admin/ConsistencyIssuesPage';
import { testTheme } from '@/test/render';

/**
 * W-3c (рішення PO 11.11): журнал узгодженості дописує сторінку; дубль за `id` між сторінками — один рядок.
 * ⛔ Червоний без фіксу: `first` зникав після кліку; без `keyOf` у `mergePages` — два рядки `first`.
 */
const SlowEnvTimeout = 400_000;

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

function issue(id: number, message: string): Record<string, unknown> {
  return {
    id,
    ruleCode: 'CNS-01',
    entityType: 'cfg.Template',
    entityId: 3,
    message,
    detectedAt: '2026-01-05T10:00:00Z',
    resolvedAt: null,
  };
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('ConsistencyIssuesPage: нескінченна прокрутка (W-3c)', () => {
  it(
    'W-3c: дописує сторінку, дубль за id — один рядок',
    async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async (input: RequestInfo | URL) => {
          const url = new URL(String(input), 'http://x');

          if (url.pathname === '/api/v1/me') return json({ permissions: [], denies: [], grants: {} });
          if (url.pathname === '/api/v1/consistency/issues') {
            return url.searchParams.get('cursor') === null
              ? json({ items: [issue(2, 'first')], nextCursor: 'c2', totalCount: 3 })
              : json({ items: [issue(2, 'first'), issue(1, 'second')], nextCursor: null, totalCount: null });
          }

          return json({ items: [], nextCursor: null, totalCount: 0 });
        }),
      );
      const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
      render(
        <MantineProvider theme={testTheme}>
          <MemoryRouter initialEntries={['/admin/consistency']}>
            <QueryClientProvider client={client}>
              <ConsistencyIssuesPage />
            </QueryClientProvider>
          </MemoryRouter>
        </MantineProvider>,
      );
      const user = userEvent.setup();

      await screen.findByText('first', {}, { timeout: SlowEnvTimeout });
      await user.click(screen.getByRole('button', { name: '⟦documents.more⟧' }));

      await screen.findByText('second', {}, { timeout: SlowEnvTimeout });
      expect(screen.getAllByText('first')).toHaveLength(1);
    },
    SlowEnvTimeout,
  );
});
