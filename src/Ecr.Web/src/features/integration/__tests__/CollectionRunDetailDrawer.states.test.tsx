import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { CollectionRunDetailDrawer } from '@/features/integration/CollectionRunDetailDrawer';
import type { CollectionRunView } from '@/features/integration/collectionRunsApi';
import { testTheme } from '@/test/render';

/**
 * Шухляда подробиць прогону: стани запиту `GET /collection-runs/{id}` —
 * «помилка», «немає права», «завантаження» (`ФВ-14.22`). «Порожньо» тут —
 * прогін без покритих інтервалів (`collectionRuns.coverageEmpty`); його вже
 * тримає `CollectionRunDetailDrawer.test.tsx` («прогін без помилки…»), тут
 * перевіряється лише, що ні відмова, ні запит у дорозі під нього не косять.
 */
configure({ asyncUtilTimeout: 10_000 });

const Run: CollectionRunView = {
  id: 501,
  sourceEntityId: 42,
  sourceEntityCode: 'FLOW-01',
  sourceEntityName: 'Flow meter #1',
  dataSourceId: 7,
  dataSourceCode: 'PI-MAIN',
  rangeFrom: '2026-09-19T00:00:00Z',
  rangeTo: '2026-09-20T00:00:00Z',
  startedAt: '2026-09-20T03:00:00Z',
  finishedAt: '2026-09-20T03:00:12Z',
  durationMs: 12000,
  status: 'Failed',
  pointsRetrieved: 0,
  isCatchUp: false,
  hasError: true,
  triggeredByUserId: null,
};

function serve(detail: () => Promise<Response>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input), 'http://localhost').pathname;

      if (path === '/api/v1/collection-runs/501') return detail();

      return new Response('null', { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

const problem = (status: number, errorCode: string): Promise<Response> =>
  Promise.resolve(
    new Response(
      JSON.stringify({
        title: status === 403 ? 'Forbidden' : 'Server error',
        status,
        errorCode,
        correlationId: 'corr-run-detail',
        detail: null,
      }),
      { status, headers: { 'Content-Type': 'application/problem+json' } },
    ),
  );

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        {/* Шухляда відкрита за `?panel=run:501` — так її відкриває журнал. */}
        <MemoryRouter initialEntries={['/admin/sources?panel=run:501']}>
          <CollectionRunDetailDrawer run={Run} panelId="run:501" />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CollectionRunDetailDrawer — стани', () => {
  it('500: помилка з кодом у шухляді, а не «інтервалів немає»', async () => {
    serve(() => problem(500, 'ECR-SYS-0500'));
    show();

    const drawer = await screen.findByRole('dialog');
    expect(await within(drawer).findByText('ECR-SYS-0500', { exact: false })).toBeTruthy();
    expect(within(drawer).queryByText('⟦collectionRuns.coverageEmpty⟧')).toBeNull();
    expect(drawer.querySelector('[data-collection-run-detail]')).toBeNull();
  });

  it('403: стан «немає права», а не подробиці чи «інтервалів немає»', async () => {
    serve(() => problem(403, 'ECR-AUTH-0403'));
    show();

    const drawer = await screen.findByRole('dialog');
    expect((await within(drawer).findByRole('alert')).textContent).toContain('ECR-AUTH-0403');
    expect(within(drawer).queryByText('⟦collectionRuns.coverageEmpty⟧')).toBeNull();
  });

  it('у дорозі: «завантаження», а не «інтервалів немає»', async () => {
    serve(() => new Promise<Response>(() => undefined));
    show();

    const drawer = await screen.findByRole('dialog');
    expect((await within(drawer).findByRole('status')).getAttribute('aria-busy')).toBe('true');
    expect(within(drawer).queryByText('⟦collectionRuns.coverageEmpty⟧')).toBeNull();
    expect(drawer.querySelector('[data-collection-run-detail]')).toBeNull();
  });
});
