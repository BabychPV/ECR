import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnapshotsPage } from '@/pages/admin/SnapshotsPage';
import { testTheme } from '@/test/render';

/**
 * Позначка формату чисел у переліку зрізів (рішення 2026-09-21).
 *
 * Числа розширено до 16 знаків після коми, а вже подані зрізи НЕ
 * перебудовуються. Старий зріз показує інше число знаків, ніж новий, і без
 * позначки різниця читається як дефект. Сервер віддає `hashFormat`:
 * `current` | `legacy` | `unknown` (`VerifyReportSnapshotHandler.Format*`).
 *
 * ⚠ Імпортується лише сторінка: тест доводить, що позначку малює САМЕ перелік,
 * а не окремий компонент у вакуумі.
 */
const project = { id: 42, code: 'KASH_2026', status: 'Active' as const };

function snapshot(id: number, hashFormat: string | undefined): Record<string, unknown> {
  return {
    id,
    builtAt: '2026-01-15T10:00:00Z',
    contentHash: `hash${String(id)}`,
    isCurrent: false,
    periodKey: 202601,
    projectId: 42,
    reportVersionId: 1,
    rowCount: 10,
    status: 'Submitted',
    ...(hashFormat === undefined ? {} : { hashFormat }),
  };
}

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function mockFetch(list: Record<string, unknown>[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: [],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (url.includes('/api/v1/reports/snapshots')) return json(list);
      if (url.includes('/api/v1/reports')) return json([]);

      if (url.includes('/api/v1/projects')) {
        return json({ items: [project], nextCursor: null, totalCount: 1 });
      }

      return json(null);
    }),
  );
}

const SlowEnvTimeout = 400_000;

async function show(list: Record<string, unknown>[]): Promise<void> {
  mockFetch(list);

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

  // Таблиця домальована: остання контрольна сума на екрані.
  await screen.findByText(`hash${String(list.length)}`, {}, { timeout: SlowEnvTimeout });
}

/** Рядок таблиці зрізу з цією контрольною сумою. */
function rowOf(id: number): HTMLElement {
  const row = screen.getByText(`hash${String(id)}`).closest('tr');

  if (row === null) throw new Error(`рядка зрізу ${String(id)} немає`);

  return row;
}

function formatBadgeIn(row: HTMLElement): HTMLElement | null {
  return row.querySelector<HTMLElement>('[data-hash-format]');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SnapshotsPage: позначка формату зрізу', () => {
  it(
    'legacy — позначка з підписом і підказкою з каталогу, тон не тривожний',
    async () => {
      await show([snapshot(1, 'legacy')]);

      const badge = formatBadgeIn(rowOf(1));

      expect(badge, 'у рядку legacy-зрізу немає позначки формату').not.toBeNull();
      expect(badge?.getAttribute('data-hash-format')).toBe('legacy');

      // Каталог не завантажено: `t()` віддає позначений ключ (`D-138`) — саме
      // він доводить, що підпис і підказка пройшли через каталог.
      expect(badge?.textContent).toBe('⟦snapshots.formatLegacy⟧');
      expect(badge?.getAttribute('title')).toBe('⟦snapshots.formatLegacyHint⟧');

      // ⛔ Не тривога: зріз не має дефекту й не вимагає дії.
      expect(badge?.getAttribute('data-format-tone')).toBe('info');

      // Позначка стоїть біля статусу, у тій самій клітинці.
      expect(badge?.closest('td')?.querySelector('[data-status-kind="snapshot"]')).not.toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'current — позначки немає (дзеркало): звичайний формат не малюється',
    async () => {
      await show([snapshot(1, 'current')]);

      expect(formatBadgeIn(rowOf(1))).toBeNull();
      expect(screen.queryByText('⟦snapshots.formatLegacy⟧')).toBeNull();
      expect(screen.queryByText('⟦snapshots.formatUnknown⟧')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'unknown — нейтральна позначка «формат невідомий», відмінна від legacy',
    async () => {
      await show([snapshot(1, 'unknown')]);

      const badge = formatBadgeIn(rowOf(1));

      expect(badge?.getAttribute('data-hash-format')).toBe('unknown');
      expect(badge?.textContent).toBe('⟦snapshots.formatUnknown⟧');
      expect(badge?.getAttribute('title')).toBe('⟦snapshots.formatUnknownHint⟧');
      expect(badge?.getAttribute('data-format-tone')).toBe('neutral');
    },
    SlowEnvTimeout,
  );

  it(
    'змішаний перелік: кожен рядок має свою позначку, current — жодної',
    async () => {
      await show([snapshot(1, 'legacy'), snapshot(2, 'current'), snapshot(3, 'unknown')]);

      expect(formatBadgeIn(rowOf(1))?.getAttribute('data-hash-format')).toBe('legacy');
      expect(formatBadgeIn(rowOf(2))).toBeNull();
      expect(formatBadgeIn(rowOf(3))?.getAttribute('data-hash-format')).toBe('unknown');
      expect(document.querySelectorAll('[data-hash-format]')).toHaveLength(2);
    },
    SlowEnvTimeout,
  );
});
