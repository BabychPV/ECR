import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, useLocation } from 'react-router-dom';
import type { JSX } from 'react';
import { CollectionRunsPanel } from '@/features/integration/CollectionRunsPanel';
import { testTheme } from '@/test/render';

/**
 * Фільтри журналу прогонів (ФВ-5.23) — стан живе в адресі (`ФВ-14.29`) і
 * ПОТРАПЛЯЄ в запит `GET /api/v1/collection-runs`.
 *
 * ⛔ Мутаційний доказ головного твердження файлу («фільтр стану — в запиті»):
 * приберіть у `CollectionRunsPanel.tsx` рядок `{ id: 'state', … }` з масиву
 * `filters` `<FilterBar>` (або підставте константне `state: null` у
 * `filters` перед побудовою запиту) — перший тест нижче падає: запит
 * лишається без `state=Failed` попри вибір у select-полі.
 */

const Entity = {
  id: 42,
  code: 'FLOW-01',
  displayName: 'Flow meter #1',
  dataSourceId: 7,
  dataSourceCode: 'PI-MAIN',
  entityPath: null,
  isActive: true,
  lastRun: null,
  oldestGap: null,
  transport: 'PiWebApi',
};

const Connection = {
  catalog: null,
  code: 'PI-MAIN',
  collectionSchedules: 0,
  endpoint: 'https://pi.example.invalid/piwebapi',
  hasSecret: false,
  id: 7,
  isActive: true,
  maxParallel: 4,
  nameL10n: { en: 'Main PI server' },
  rowVersion: 'AAAAAAAAB9E=',
  secondaryEndpoint: null,
  sourceEntities: 1,
  transport: 'PiWebApi',
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function respond(): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    const path = new URL(String(input), 'http://localhost').pathname;

    if (path === '/api/v1/collection-runs') return json({ items: [], nextCursor: null, totalCount: null });
    if (path === '/api/v1/data-sources') return json([Connection]);
    if (path === '/api/v1/sources') return json([Entity]);

    return json(null, 404);
  });

  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

function requestUrls(fetchMock: ReturnType<typeof vi.fn>): string[] {
  return fetchMock.mock.calls
    .map((call) => String(call[0]))
    .filter((url) => url.includes('/api/v1/collection-runs?'));
}

function Location(): JSX.Element {
  useLocation();
  return <></>;
}

function show(initial = '/admin/sources'): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[initial]}>
          <CollectionRunsPanel />
          <Location />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CollectionRunsPanel: фільтр стану доходить до запиту', () => {
  it('вибір стану в FilterBar додає state= до GET /api/v1/collection-runs', async () => {
    const fetchMock = respond();
    show();

    await waitFor(() => expect(requestUrls(fetchMock).length).toBeGreaterThan(0));

    // ⟦collectionRuns.filterState⟧ — позначений ключ підпису поля (немає в
    // сіді цією задачею навмисно, D-138), і саме ним фільтр знаходиться.
    await screen.findByLabelText('⟦collectionRuns.filterState⟧');
    fetchMock.mockClear();

    fireEvent.click(screen.getByLabelText('⟦collectionRuns.filterState⟧'));
    fireEvent.click(await screen.findByRole('option', { name: '⟦collectionRuns.stateFailed⟧' }));

    await waitFor(() => {
      const urls = requestUrls(fetchMock);
      expect(urls.some((url) => url.includes('state=Failed'))).toBe(true);
    });
  });

  it('фільтр джерела звужує перелік сутностей до цього зʼєднання', async () => {
    respond();
    show('/admin/sources?dataSource=7');

    // Одна сутність належить з'єднанню 7 — поле сутності показує саме її.
    await screen.findByLabelText('⟦collectionRuns.filterEntity⟧');
    fireEvent.click(screen.getByLabelText('⟦collectionRuns.filterEntity⟧'));

    expect(await screen.findByRole('option', { name: 'Flow meter #1 (FLOW-01)' })).toBeTruthy();
  });

  it('діапазон дат перетворюється на UTC-межі "from" включно і "to" виключно', async () => {
    const fetchMock = respond();
    show('/admin/sources?from=2026-09-01&to=2026-09-03');

    await waitFor(() => {
      const urls = requestUrls(fetchMock);
      expect(urls.length).toBeGreaterThan(0);
      const url = urls[urls.length - 1] ?? '';

      // `from` — початок 1 вересня UTC (включно); `to` — початок 4 вересня UTC
      // (виключно), тобто «до 3 вересня включно», а не до 3-го опівночі.
      expect(url).toContain(encodeURIComponent('2026-09-01T00:00:00.000Z'));
      expect(url).toContain(encodeURIComponent('2026-09-04T00:00:00.000Z'));
    });
  });
});
