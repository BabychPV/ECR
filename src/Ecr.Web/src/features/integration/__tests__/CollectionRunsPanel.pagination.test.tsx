import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { CollectionRunsPanel } from '@/features/integration/CollectionRunsPanel';

/**
 * Курсорна пагінація журналу (ФВ-5.23) — «показати ще» ДОЧИТУЄ, а не
 * гортає: перша сторінка лишається на екрані разом із другою
 * (`useInfiniteQuery`, той самий патерн, що `DeliveriesPanel.tsx`).
 */

function run(id: number): Record<string, unknown> {
  return {
    id,
    sourceEntityId: id,
    sourceEntityCode: `FLOW-${id}`,
    sourceEntityName: `Flow meter #${id}`,
    dataSourceId: 7,
    dataSourceCode: 'PI-MAIN',
    rangeFrom: '2026-09-19T00:00:00Z',
    rangeTo: '2026-09-20T00:00:00Z',
    startedAt: '2026-09-20T03:00:00Z',
    finishedAt: '2026-09-20T03:00:12Z',
    durationMs: 1000,
    status: 'Succeeded',
    pointsRetrieved: 10,
    isCatchUp: false,
    hasError: false,
    triggeredByUserId: null,
  };
}

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function respond(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), 'http://localhost');

      if (url.pathname === '/api/v1/collection-runs') {
        const cursor = url.searchParams.get('cursor');

        if (cursor === null) {
          return json({ items: [run(1)], nextCursor: 'page-2', totalCount: null });
        }

        return json({ items: [run(2)], nextCursor: null, totalCount: null });
      }

      if (url.pathname === '/api/v1/data-sources') return json([]);
      if (url.pathname === '/api/v1/sources') return json([]);

      return json(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/sources']}>
          <CollectionRunsPanel />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CollectionRunsPanel: "показати ще" дочитує курсор', () => {
  it('перша сторінка показує nextCursor кнопкою; клік довантажує ДРУГУ, не замінюючи першу', async () => {
    respond();
    show();

    await screen.findByText('Flow meter #1');
    expect(screen.queryByText('Flow meter #2')).toBeNull();

    const button = document.querySelector<HTMLElement>('[data-collection-runs-more]');
    expect(button, 'кнопка "показати ще"').not.toBeNull();

    fireEvent.click(button as HTMLElement);

    await screen.findByText('Flow meter #2');
    // ⛔ Мутаційний доказ: заміна `useInfiniteQuery`-накопичення на просту
    // заміну сторінки (перезапис `rows` останньою відповіддю) прибрала б
    // рядок #1 з екрана — цей рядок падає.
    expect(screen.getByText('Flow meter #1')).toBeTruthy();

    // Курсору більше немає — кнопки теж.
    await waitFor(() => expect(document.querySelector('[data-collection-runs-more]')).toBeNull());

    const row1 = document.querySelector<HTMLElement>('tr[data-row-key="1"]');
    const row2 = document.querySelector<HTMLElement>('tr[data-row-key="2"]');
    expect(row1).not.toBeNull();
    expect(row2).not.toBeNull();
    expect(within(row1 as HTMLElement).getByText('PI-MAIN')).toBeTruthy();
  });
});
