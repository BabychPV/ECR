import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CellSourceSection } from '../CellSourceSection';
import { testTheme } from '@/test/render';
import type { CellSourceView } from '../api';

/**
 * D-463 «Звідки це значення»: секція комірки в інспекторі. Комірка без мапінгу — секції немає (порожній перелік
 * сервера); з мапінгом — тег, згортка, вікно періоду, подія покриття і кнопка сирих точок із кількістю. Відмова
 * доступу (404) — секції теж немає: клієнт нічого не вирішує сам і не показує «помилку» там, де людині нема що бачити.
 */
const Cell = { documentId: 7, tableInstanceId: 70, rowKey: 'R1', columnDefId: 5 };

const Fed: CellSourceView = {
  periodKey: 202601,
  windowPeriodKey: 202601,
  windowMonth: 3,
  periodFromUtc: '2026-02-28T19:00:00Z',
  periodToUtc: '2026-03-31T19:00:00Z',
  sources: [
    {
      mapId: 11,
      sourceEntityId: 3,
      sourceEntityCode: 'STACK_01',
      sourceEntityName: 'Stack 01',
      sourcePath: '\\\\AF\\Plant\\Stack01|CO',
      aggregation: 'Sum',
      sourceUnitCode: 'kg',
      targetUnitCode: 't',
      isActive: false,
      pointCount: 744,
      lastCollection: {
        lastRunId: 90,
        lastRunStatus: 'Succeeded',
        lastRunStartedAt: '2026-02-01T00:00:00Z',
        lastRunFinishedAt: '2026-02-01T00:01:00Z',
        lastSucceededAt: '2026-02-01T00:01:00Z',
      },
      events: [{ id: 1, status: 'PartialCoverage', details: 'old free text', at: '2026-02-01T00:02:00Z' }],
      fold: {
        state: 'Value',
        value: '2294.0000000000000000',
        percentGood: '100',
        storedValue: '2000',
        storedOrigin: 'UserEdit',
        storedAt: '2026-04-01T00:00:00Z',
        comparison: 'ManualEdit',
      },
    },
  ],
};

const requested: string[] = [];

const EmptySeries = {
  fromUtc: Fed.periodFromUtc,
  toUtc: Fed.periodToUtc,
  bucketSeconds: 5357,
  totalPoints: 0,
  numericPoints: 0,
  buckets: [],
};

function serve(status: number, body: unknown): void {
  requested.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => {
      requested.push(url);
      const payload = url.includes('/cell-source/series')
        ? EmptySeries
        : url.includes('/cell-source/points')
          ? { items: [], nextCursor: null, totalCount: null }
          : body;
      return new Response(JSON.stringify(payload), { status, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <CellSourceSection cell={Cell} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('CellSourceSection', () => {
  it('показує тег, згортку, призупинення, подію й кнопку сирих точок — запит рівно цієї комірки', async () => {
    serve(200, Fed);
    show();

    expect(await screen.findByText('\\\\AF\\Plant\\Stack01|CO')).toBeTruthy();
    expect(screen.getByText('Sum')).toBeTruthy();
    expect(screen.getByText('⟦cellSource.paused⟧')).toBeTruthy();
    expect(screen.getByText('kg → t')).toBeTruthy();
    expect(screen.getByText('old free text')).toBeTruthy();
    expect(screen.getByRole('button', { name: /cellSource\.showPoints/ })).toBeTruthy();

    // D-462: колонка місяця — вікно названо місяцем; значення за правилом і в комірці з ознакою розбіжності.
    expect(document.querySelector('[data-cell-source-window="3"]')).not.toBeNull();
    expect(screen.getByText(/cellSource\.windowMonth/)).toBeTruthy();
    expect(screen.getByText('2294 t')).toBeTruthy();
    expect(screen.getByText('2000')).toBeTruthy();
    expect(document.querySelector('[data-cell-source-comparison="ManualEdit"]')?.textContent).toBe('⟦cellSource.compareManualEdit⟧');

    expect(requested[0]).toBe('/api/v1/documents/7/tables/70/cell-source?rowKey=R1&columnDefId=5');
  });

  it('комірка без мапінгу — секції немає', async () => {
    serve(200, { ...Fed, sources: [] });
    show();

    await waitFor(() => expect(requested).toHaveLength(1));
    expect(screen.queryByText('⟦cellSource.title⟧')).toBeNull();
  });

  it('відмова доступу (404) — секції немає і без повторів', async () => {
    serve(404, { status: 404, errorCode: 'ECR-DOC-0404', messageKey: 'err.ECR-DOC-0404.document' });
    show();

    await waitFor(() => expect(requested).toHaveLength(1));
    expect(screen.queryByText('⟦cellSource.title⟧')).toBeNull();
  });

  it('кнопка відкриває вікно з графіком і точками періоду цієї комірки', async () => {
    serve(200, Fed);
    show();

    fireEvent.click(await screen.findByRole('button', { name: /cellSource\.showPoints/ }));

    await waitFor(() =>
      expect(requested.some((url) => url.startsWith('/api/v1/documents/7/tables/70/cell-source/series?') && url.includes('mapId=11'))).toBe(true),
    );
    expect(requested.some((url) => url.startsWith('/api/v1/documents/7/tables/70/cell-source/points?'))).toBe(true);
  });
});
