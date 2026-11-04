import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { PiDataPage } from '@/pages/admin/PiDataPage';
import { testTheme } from '@/test/render';

/**
 * D-463 «Дані з PI»: сторінка за адресою з сутністю — теги, графік (лінивий чанк), покриття з прогалиною, сирі
 * точки й події; без сутності — підказка і жодного запиту даних.
 */
const requested: string[] = [];

function respond(url: string): unknown {
  if (url === '/api/v1/sources') return [{ id: 3, code: 'STACK_01', displayName: 'Stack 01', dataSourceId: 1 }];
  if (url.startsWith('/api/v1/sources/3/raw-data/tags'))
    return {
      sourceEntityId: 3,
      code: 'STACK_01',
      displayName: 'Stack 01',
      isActive: true,
      isTruncated: false,
      tags: [
        { sourcePath: 'Stack01|CO', mapId: 1, targetColumnCode: 'CO', targetRowKey: 'R1', aggregation: 'Sum', sourceUnitCode: 'kg', isActive: true },
        { sourcePath: 'Stack01|NOx', mapId: 2, targetColumnCode: 'NOX', targetRowKey: 'R2', aggregation: 'Avg', sourceUnitCode: null, isActive: true },
      ],
    };
  if (url.startsWith('/api/v1/sources/3/raw-data/series'))
    return {
      fromUtc: '2026-10-24T12:00:00Z',
      toUtc: '2026-10-31T12:00:00Z',
      bucketSeconds: 1210,
      totalPoints: 2,
      numericPoints: 2,
      buckets: [{ startUtc: '2026-10-25T00:00:00Z', points: 2, min: '1.5', max: '2.5', avg: '2' }],
    };
  if (url.startsWith('/api/v1/sources/3/raw-data/coverage'))
    return {
      fromUtc: '2026-10-24T12:00:00Z',
      toUtc: '2026-10-31T12:00:00Z',
      covered: [{ fromUtc: '2026-10-24T12:00:00Z', toUtc: '2026-10-30T12:00:00Z' }],
      gaps: [{ fromUtc: '2026-10-30T12:00:00Z', toUtc: '2026-10-31T12:00:00Z' }],
      isTruncated: false,
      lastCollection: {
        lastRunId: 9,
        lastRunStatus: 'Failed',
        lastRunStartedAt: '2026-10-31T00:00:00Z',
        lastRunFinishedAt: '2026-10-31T00:00:05Z',
        lastSucceededAt: '2026-10-30T12:00:00Z',
      },
    };
  if (url.startsWith('/api/v1/sources/3/raw-data/points'))
    return {
      items: [
        {
          timestamp: '2026-10-25T00:30:00Z',
          valueNumeric: '2.5000000000000000',
          valueString: null,
          unitCode: 'kg',
          quality: 'Good',
          retrievedAt: '2026-10-25T01:00:00Z',
          collectionRunId: 8,
        },
      ],
      nextCursor: null,
      totalCount: null,
    };
  if (url.startsWith('/api/v1/collection-runs/coverage-events')) return { items: [], nextCursor: null, totalCount: null };
  return null;
}

function show(path: string): void {
  requested.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => {
      requested.push(url);
      return new Response(JSON.stringify(respond(url)), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[path]}>
          <PiDataPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('PiDataPage', () => {
  it('без сутності — підказка, запитів даних немає', async () => {
    show('/admin/pi-data');

    expect(await screen.findByText('⟦piData.pickHint⟧')).toBeTruthy();
    await waitFor(() => expect(requested).toContain('/api/v1/sources'));
    expect(requested.some((url) => url.includes('/raw-data/'))).toBe(false);
  });

  it('сутність з адреси: тег за замовчуванням — перший, покриття з прогалиною, точка без хвостових нулів, події', async () => {
    show('/admin/pi-data?entity=3');

    expect(await screen.findByText('2.5')).toBeTruthy();
    expect(screen.getByText('⟦piData.gaps (count=1)⟧')).toBeTruthy();
    expect(screen.getByText('⟦coverageEvents.empty⟧')).toBeTruthy();

    const points = requested.find((url) => url.startsWith('/api/v1/sources/3/raw-data/points?'));
    expect(points).toBeDefined();
    const query = new URLSearchParams(points?.split('?')[1]);
    expect(query.get('path')).toBe('Stack01|CO');
    expect(query.get('limit')).toBe('100');
    expect(requested.some((url) => url.startsWith('/api/v1/sources/3/raw-data/series?') && url.includes('buckets=500'))).toBe(true);
    expect(requested.some((url) => url.startsWith('/api/v1/collection-runs/coverage-events?entity=3'))).toBe(true);
  });

  it('тег і пресет з адреси йдуть у запит; рік — межі року UTC', async () => {
    show('/admin/pi-data?entity=3&tag=Stack01%7CNOx&range=lastYear');

    await waitFor(() => expect(requested.some((url) => url.startsWith('/api/v1/sources/3/raw-data/series?'))).toBe(true));
    const series = requested.find((url) => url.startsWith('/api/v1/sources/3/raw-data/series?')) ?? '';
    const query = new URLSearchParams(series.split('?')[1]);
    const year = new Date().getUTCFullYear();

    expect(query.get('path')).toBe('Stack01|NOx');
    expect(query.get('fromUtc')).toBe(`${String(year - 1)}-01-01T00:00:00.000Z`);
    expect(query.get('toUtc')).toBe(`${String(year)}-01-01T00:00:00.000Z`);
  });
});
