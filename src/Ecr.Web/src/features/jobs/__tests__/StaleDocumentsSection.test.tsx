import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { StaleDocumentsSection } from '@/features/jobs/StaleDocumentsSection';

/**
 * Блок «Потребують перерахунку»: запит іде лише відкритим періодам, `staleBy=me`, посилання несе `periodKey`.
 * Мутація: приберіть `staleBy: 'me'` в `useStaleDocuments.ts` або `periodKey` у `staleDocumentHref` - тест червоний.
 */

const calls: string[] = [];

function respond(url: string): unknown {
  if (url.startsWith('/api/v1/projects/7/periods')) {
    return {
      periods: [
        { periodKey: 202609, state: 'Closed', isCurrent: false },
        { periodKey: 202610, state: 'Open', isCurrent: true },
      ],
    };
  }
  if (url.startsWith('/api/v1/projects')) return { items: [{ id: 7 }], nextCursor: null, totalCount: 1 };
  if (url.startsWith('/api/v1/documents')) {
    return {
      items: [{ id: 5, businessKey: 'AIR-2026-10', resultsStale: true, resultsStaleSince: '2026-10-07T09:30:00Z' }],
      nextCursor: null,
      totalCount: 1,
    };
  }

  return {};
}

function show(opened: boolean): void {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.pathname + input.search : input.url;
      calls.push(url);

      return Promise.resolve(
        new Response(JSON.stringify(respond(url)), { status: 200, headers: { 'Content-Type': 'application/json' } }),
      );
    }),
  );
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter>
          <StaleDocumentsSection opened={opened} />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  calls.length = 0;
  vi.unstubAllGlobals();
});

describe('StaleDocumentsSection', () => {
  it('показує документ зі застарілими результатами з посиланням на його період', async () => {
    show(true);

    const section = await screen.findByTestId('my-tasks-stale');
    expect(section.textContent).toContain('AIR-2026-10');
    expect(section.querySelector('a')?.getAttribute('href')).toBe('/documents/5?periodKey=202610');

    const listCalls = calls.filter((url) => url.includes('/documents'));
    expect(listCalls).toHaveLength(1);
    expect(listCalls[0]).toContain('periodKey=202610');
    expect(listCalls[0]).toContain('resultsStale=true');
    expect(listCalls[0]).toContain('staleBy=me');
  });

  it('поки шухляда закрита, жодного запиту', async () => {
    show(false);

    await waitFor(() => expect(screen.queryByTestId('my-tasks-stale')).toBeNull());
    expect(calls).toHaveLength(0);
  });
});
