import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { CollectionRunsPanel } from '@/features/integration/CollectionRunsPanel';

/**
 * Шухляда подробиць прогону (ФВ-5.23): помилка (якщо `hasError`) і покриті
 * інтервали, з банером усічення понад 500.
 *
 * ⛔ Мутаційний доказ головного твердження файлу («banner усічення видно»):
 * приберіть у `CollectionRunDetailDrawer.tsx` умову
 * `{detail.coverageTruncated && <Alert … data-coverage-truncated>}` (замініть
 * на завжди `false` чи видаліть блок) — другий тест нижче падає:
 * `data-coverage-truncated` не знаходиться в DOM попри `coverageTruncated:
 * true` у відповіді сервера.
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
  durationMs: 12000,
  status: 'Failed',
  pointsRetrieved: 0,
  isCatchUp: false,
  hasError: true,
  triggeredByUserId: null,
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function respond(detail: unknown): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input), 'http://localhost').pathname;

      if (path === '/api/v1/collection-runs') {
        return json({ items: [Run], nextCursor: null, totalCount: null });
      }
      if (path === '/api/v1/collection-runs/501') return json(detail);
      if (path === '/api/v1/data-sources') return json([]);
      if (path === '/api/v1/sources') return json([]);

      return json(null);
    }),
  );
}

function show(entry = '/admin/sources'): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[entry]}>
          <CollectionRunsPanel />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function openDrawer(): Promise<void> {
  // ⚠ Тайм-аут довший за дефолтний (1 с): під повним набором (2000+ тестів,
  // паралельні воркери) цей самий запит під навантаженням CPU інколи не
  // встигає резолвитися за секунду — не логічна вада, а запас на конкуренцію
  // за процесор (`CLAUDE.md`: «npm test, CPU < 80%»).
  await screen.findByText('Flow meter #1', {}, { timeout: 5000 });
  const row = document.querySelector<HTMLElement>('tr[data-row-key="501"]');
  fireEvent.click(row as HTMLElement);
  await screen.findByRole('dialog', {}, { timeout: 5000 });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CollectionRunDetailDrawer', () => {
  it('клік на рядок відкриває шухляду й показує текст помилки, коли hasError', async () => {
    respond({
      run: Run,
      errorMessage: 'Джерело недоступне: тайм-аут з’єднання.',
      coverage: [],
      coverageTruncated: false,
    });
    show();

    await openDrawer();

    expect(await screen.findByText('Джерело недоступне: тайм-аут з’єднання.')).toBeTruthy();
  });

  it('coverageTruncated показує банер "показано не все"', async () => {
    respond({
      run: Run,
      errorMessage: null,
      coverage: [{ coveredFrom: '2026-09-19T00:00:00Z', coveredTo: '2026-09-19T12:00:00Z' }],
      coverageTruncated: true,
    });
    show();

    await openDrawer();

    await waitFor(() =>
      expect(document.querySelector('[data-coverage-truncated]')).not.toBeNull(),
    );
  });

  it('coverageTruncated=false НЕ показує банер', async () => {
    respond({
      run: Run,
      errorMessage: null,
      coverage: [{ coveredFrom: '2026-09-19T00:00:00Z', coveredTo: '2026-09-19T12:00:00Z' }],
      coverageTruncated: false,
    });
    show();

    await openDrawer();

    // Даємо запиту деталі домалюватись (перелік інтервалів — ознака завершення).
    await waitFor(() => expect(document.querySelector('[data-coverage-list]')).not.toBeNull());
    expect(document.querySelector('[data-coverage-truncated]')).toBeNull();
  });

  it('прогін без помилки не показує алерт помилки', async () => {
    respond({
      run: { ...Run, hasError: false, status: 'Succeeded' },
      errorMessage: null,
      coverage: [],
      coverageTruncated: false,
    });
    show();

    await openDrawer();

    await screen.findByText('⟦collectionRuns.coverageEmpty⟧');
    expect(screen.queryByText('⟦collectionRuns.error⟧')).toBeNull();
  });
});
