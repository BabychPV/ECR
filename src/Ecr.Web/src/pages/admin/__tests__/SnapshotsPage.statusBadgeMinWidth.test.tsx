import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnapshotsPage } from '@/pages/admin/SnapshotsPage';
import { testTheme } from '@/test/render';

/**
 * Аудит-пас 8, lane6, п.9: той самий клас дефекту, що вже виправлений для
 * бейджа стану `PeriodsPage.tsx` (`PeriodsPage.stateBadgeMinWidth.test.tsx`,
 * lane8) — `table-layout: auto` бере ширину стовпця з того, що фактично
 * РЕНДЕРИТЬСЯ, а `.mantine-Badge-label`'s власний `overflow: hidden` дозволяє
 * йому «поміститись» у будь-яку ширину. Наслідок тут: «DRAFT» ставало
 * нечитабельним «D…» на типовій ширині вікна замість перемикання ScrollArea
 * на горизонтальну прокрутку.
 *
 * ⛔ Мутаційний доказ структурний (jsdom не рахує layout): перевіряється, що
 * САМЕ той інлайн-стиль, який лікує дефект (`min-width: fit-content`),
 * застосований РІВНО на бейджі статусу зрізу.
 */
const project = { id: 42, code: 'KASH_2026', status: 'Active' as const };

const snapshot = {
  id: 1,
  builtAt: '2026-01-15T10:00:00Z',
  contentHash: 'abc123',
  isCurrent: false,
  periodKey: 202601,
  projectId: 42,
  reportVersionId: 1,
  rowCount: 10,
  status: 'Draft',
};

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return new Response(
          JSON.stringify({
            denies: [],
            grants: {},
            isSimulation: false,
            language: 'en',
            mustChangePassword: false,
            permissions: [],
            simulatedForUserId: null,
            userId: 1,
            userName: 'tester',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/api/v1/reports/snapshots')) {
        return new Response(JSON.stringify([snapshot]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/api/v1/reports')) {
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/api/v1/projects')) {
        return new Response(JSON.stringify({ items: [project], nextCursor: null, totalCount: 1 }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(null), { status: 200 });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/snapshots']}>
        <QueryClientProvider client={client}>
          <SnapshotsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('SnapshotsPage: бейдж статусу не стискається нижче власного тексту (аудит-пас 8)', () => {
  it(
    'бейдж «Draft» має min-width: fit-content',
    async () => {
      mockFetch();
      show();

      const badge = await screen.findByText('Draft', {}, { timeout: SlowEnvTimeout });
      const badgeRoot = badge.closest('.mantine-Badge-root') as HTMLElement | null;

      expect(badgeRoot).not.toBeNull();
      // ⛔ Мутаційний доказ: без цього стилю таблиця (100% ширини контейнера)
      // стискає бейдж до нечитабельного «D…» замість перемикання ScrollArea
      // на горизонтальну прокрутку.
      expect(badgeRoot?.style.minWidth).toBe('fit-content');
    },
    SlowEnvTimeout,
  );
});
