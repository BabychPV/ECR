import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { CollectionRunsPanel } from '@/features/integration/CollectionRunsPanel';

/**
 * Журнал прогонів збору (ФВ-5.23) — рядок таблиці.
 *
 * ⛔ Доказ ЧЕРВОНОГО до змін: до цієї картки `CollectionRunsPanel.tsx` не
 * існував і не мав жодного споживача — `GET /api/v1/collection-runs` не
 * викликав ЖОДЕН файл клієнта. Цей файл на чистому `feat/collection-runs`
 * (без клієнтського коду) падає на самому імпорті
 * (`Cannot find module '@/features/integration/CollectionRunsPanel'`), тобто
 * ще ДО першого твердження — той самий клас доказу, що в
 * `DataSourcesTable.test.tsx` («не було жодного рядка з'єднання»).
 */

const Run = {
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
  durationMs: 12345,
  status: 'Succeeded',
  pointsRetrieved: 2880,
  isCatchUp: false,
  hasError: false,
  triggeredByUserId: null,
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function respond(runsBody: unknown, runsStatus = 200): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input), 'http://localhost').pathname;

      if (path === '/api/v1/collection-runs') return json(runsBody, runsStatus);
      if (path === '/api/v1/data-sources') return json([]);
      if (path === '/api/v1/sources') return json([]);

      return json(null, 404);
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

describe('CollectionRunsPanel: рядок журналу', () => {
  it('показує сутність, стан, тривалість, точки і "система" для запуску без користувача', async () => {
    respond({ items: [Run], nextCursor: null, totalCount: null });
    show();

    await screen.findByText('Flow meter #1');

    const row = document.querySelector<HTMLElement>('tr[data-row-key="501"]');
    expect(row, 'рядок прогону 501').not.toBeNull();
    const scoped = within(row as HTMLElement);

    expect(scoped.getByText('PI-MAIN')).toBeTruthy();
    // ⛔ Мутаційний доказ на бейдж: `Тones.Succeeded` дає варту, ключ бракує в
    // сіді (`09-seed.sql` не чіпаємо цією задачею), тому підпис — позначений
    // ключ `⟦collectionRuns.stateSucceeded⟧` (`D-138`); заміна ключа на інший
    // валить цей рядок.
    expect(scoped.getByText('⟦collectionRuns.stateSucceeded⟧')).toBeTruthy();

    // 12345 мс → 12.3 с.
    expect(row?.textContent ?? '').toContain('12.3');
    // Роздільник розрядів — `formatNumber`, а не сире `2880`.
    expect(row?.textContent ?? '').toContain('2,880');
    expect(scoped.getByText('⟦collectionRuns.system⟧')).toBeTruthy();
  });

  it('запуск людиною показує ідентифікатор користувача, а не "система"', async () => {
    respond({ items: [{ ...Run, id: 502, triggeredByUserId: 9 }], nextCursor: null, totalCount: null });
    show();

    await screen.findByText('Flow meter #1');

    const row = document.querySelector<HTMLElement>('tr[data-row-key="502"]');
    expect(within(row as HTMLElement).getByText('9')).toBeTruthy();
    expect(within(row as HTMLElement).queryByText('⟦collectionRuns.system⟧')).toBeNull();
  });

  it('L10: відмова переліку — не "журнал порожній"', async () => {
    // ⚠ `HTTP-500`, а не вигаданий код каталогу (`ClientErrorCodeTests`):
    // саме цю форму `client.ts` (`problemBodyOf`) підставляє сам, коли
    // відповідь не `problem+json`, — вона очевидно не з каталогу.
    respond({ title: 'Server error', status: 500, correlationId: 'c-1', errorCode: 'HTTP-500' }, 500);
    show();

    const section = await waitFor(() => {
      const node = document.querySelector<HTMLElement>('[data-collection-runs-panel] [data-table-state]');
      expect(node).not.toBeNull();
      return node as HTMLElement;
    });

    await waitFor(() => expect(within(section).getByRole('alert')).toBeTruthy());
    expect(section.textContent ?? '').not.toContain('collectionRuns.empty');
  });

  it('422 невалідного стану фільтра — причина сервера ПІД фільтром, а не «порожньо»', async () => {
    // ⚠ Той самий контракт, що й `ListCollectionRunsHandler.Invalid`
    // (`application/problem+json`, RFC 9457 §3.2): розширення — ПЛОСКІ поля
    // верхнього рівня тіла, не вкладений об'єкт `extensions2` (`client.ts`,
    // `problemBodyOf`: усе, що не входить у стандартні `type/title/status/
    // detail/instance`, саме й СТАЄ `problem.extensions2`). `messageKey` тут —
    // ознака, за якою клієнт (`problemText.ts`) показує `detail` як є.
    respond(
      {
        title: 'err.ECR-REQ-0422',
        status: 422,
        correlationId: 'c-422',
        errorCode: 'ECR-REQ-0422',
        detail: 'Стану прогону «bogus» не існує.',
        messageKey: 'err.ECR-REQ-0422.collectionRunState',
        state: 'bogus',
      },
      422,
    );
    show();

    // ⚠ `DataTable` малює `ErrorAlert` ОДРАЗУ під `<FilterBar>` (той самий
    // `<Stack>`) — рядок нижче й перевіряє, що причина видима саме там, а не
    // замінена порожнім станом переліку.
    expect(await screen.findByText('Стану прогону «bogus» не існує.')).toBeTruthy();
    expect(screen.queryByText('⟦collectionRuns.empty⟧')).toBeNull();
  });
});
