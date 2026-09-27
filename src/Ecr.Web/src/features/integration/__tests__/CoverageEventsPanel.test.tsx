import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { CoverageEventsPanel } from '@/features/integration/CoverageEventsPanel';

/**
 * Події журналу покриття (ІНТ-3.3, `D-118`) на `/admin/sources`.
 *
 * ⛔ До цієї секції статусні рядки `itg.CollectionCoverage` не показував ЖОДЕН
 * екран: `GET /api/v1/collection-runs/coverage-events` не існувало, а деталь
 * прогону їх не бачить (`CollectionRunId = null`, Q-186).
 */

const Ceiling = {
  id: 9001,
  sourceEntityId: 42,
  sourceEntityCode: 'FLOW-01',
  sourceEntityName: 'Flow meter #1',
  dataSourceCode: 'PI-MAIN',
  periodKey: 202609,
  status: 'SkippedPointCeiling',
  details: 'Field TOTAL: 120000 points, ceiling 100000.',
  at: '2026-09-20T03:00:00Z',
};

const Closed = {
  ...Ceiling,
  id: 9000,
  sourceEntityName: null,
  sourceEntityCode: 'FLOW-02',
  status: 'SkippedPeriodClosed',
  details: null,
};

const EventsPath = '/api/v1/collection-runs/coverage-events';

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

/** Підміняє fetch; повертає перелік параметрів кожного запиту до подій. */
function respond(pages: (cursor: string | null) => unknown): URLSearchParams[] {
  const seen: URLSearchParams[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), 'http://localhost');

      if (url.pathname === EventsPath) {
        seen.push(url.searchParams);
        return json(pages(url.searchParams.get('cursor')));
      }

      return json(null, 404);
    }),
  );

  return seen;
}

function show(entry = '/admin/sources'): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[entry]}>
          <CoverageEventsPanel />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CoverageEventsPanel', () => {
  it('рядок несе сутність, період, бейдж статусу і пояснення сервера як є', async () => {
    respond(() => ({ items: [Ceiling, Closed], nextCursor: null, totalCount: null }));
    show();

    await screen.findByText('Flow meter #1');

    const row = document.querySelector<HTMLElement>('tr[data-row-key="9001"]');
    expect(row, 'рядок події 9001').not.toBeNull();
    const scoped = within(row as HTMLElement);

    expect(scoped.getByText('PI-MAIN')).toBeTruthy();
    expect(scoped.getByText('202609')).toBeTruthy();
    expect(scoped.getByText('Field TOTAL: 120000 points, ceiling 100000.')).toBeTruthy();

    // ⛔ Статус — бейдж набору з тоном, а не сирий код сервера.
    const badge = (row as HTMLElement).querySelector<HTMLElement>('[data-status-kind="coverage"]');
    expect(badge?.dataset.statusState).toBe('SkippedPointCeiling');
    expect(badge?.dataset.statusTone).toBe('danger');
    expect(badge?.textContent).toBe('⟦status.coverage.SkippedPointCeiling⟧');

    // Сутність без назви — кодом; пояснення без тексту — тире.
    const other = within(document.querySelector<HTMLElement>('tr[data-row-key="9000"]') as HTMLElement);
    expect(other.getByText('FLOW-02')).toBeTruthy();
    expect(
      (document.querySelector('tr[data-row-key="9000"] [data-status-kind="coverage"]') as HTMLElement).dataset
        .statusTone,
    ).toBe('warning');
  });

  it('фільтр статусу з адреси (`coverageStatus`) іде в запит як `status`', async () => {
    const seen = respond(() => ({ items: [Closed], nextCursor: null, totalCount: null }));
    show('/admin/sources?coverageStatus=SkippedPeriodClosed&state=Failed');

    await screen.findByText('FLOW-02');

    expect(seen.length).toBeGreaterThan(0);
    expect(seen.every((params) => params.get('status') === 'SkippedPeriodClosed')).toBe(true);
  });

  it('«Показати ще» дочитує за курсором і зникає, коли курсора немає', async () => {
    const seen = respond((cursor) =>
      cursor === null
        ? { items: [Ceiling], nextCursor: 'c-1', totalCount: null }
        : { items: [Closed], nextCursor: null, totalCount: null },
    );
    show();

    await screen.findByText('Flow meter #1');
    const more = document.querySelector<HTMLElement>('[data-coverage-events-more]');
    expect(more).not.toBeNull();

    fireEvent.click(more as HTMLElement);

    await screen.findByText('FLOW-02');
    expect(screen.getByText('Flow meter #1')).toBeTruthy();
    expect(seen.at(-1)?.get('cursor')).toBe('c-1');
    await waitFor(() => expect(document.querySelector('[data-coverage-events-more]')).toBeNull());
  });

  it('порожній журнал — власний порожній стан, без кнопки дочитування', async () => {
    respond(() => ({ items: [], nextCursor: null, totalCount: null }));
    show();

    expect(await screen.findByText('⟦coverageEvents.empty⟧')).toBeTruthy();
    expect(document.querySelector('[data-coverage-events-more]')).toBeNull();
  });
});
