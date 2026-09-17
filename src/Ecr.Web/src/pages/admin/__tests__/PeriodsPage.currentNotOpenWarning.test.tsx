import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { testTheme } from '@/test/render';

/**
 * UI-аудит, lane 2: `lane2-make-current-closed-period.md` — «Make current»
 * дозволяє призначити поточним будь-який період незалежно від його стану
 * (`D-77`: `CurrentPeriod` — підказка інтерфейсу, а НЕ правило доступу — це
 * свідоме рішення, не дефект). Але екран не давав жодного натяку, коли
 * «current» опинявся на періоді, що не Open: адмін бачив самé «current»,
 * без жодного попередження, що це не той період, який справді відкритий
 * для правки.
 *
 * Тест доводить: попередження з'являється РІВНО на періоді, що одночасно
 * `isCurrent` і НЕ `Open`, і РІВНО НЕ з'являється ні на Open-поточному, ні
 * на не-поточному Closed (інакше «показувати завжди» чи «завжди ховати»
 * пройшли б так само, як і правильний фікс).
 */
const project = {
  id: 7,
  code: 'PRJ-7',
  status: 'Active' as const,
  periodKind: 'Monthly' as const,
  periodCount: 2,
  currentPeriodId: null,
  timeZoneId: 'Europe/Kyiv',
};

const calendar = {
  projectId: 7,
  periodKind: 'Monthly' as const,
  currentPeriodMode: 'Pinned' as const,
  timeZoneId: 'Europe/Kyiv',
  // Q-337, lane 2: `PeriodCalendarDto.Policy` — тултипи заголовків «Range»/
  // «Grace until» тепер підставляють ці числа.
  policy: { id: 1, code: 'ECR-Standard', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 45 },
  periods: [
    {
      id: 1,
      periodKey: 202601,
      sequence: 1,
      startsAt: '2026-01-01',
      endsAt: '2026-02-01',
      state: 'Closed',
      graceEndsAt: null,
      reopenedUntil: null,
      isCurrent: true,
    },
    {
      id: 2,
      periodKey: 202602,
      sequence: 2,
      startsAt: '2026-02-01',
      endsAt: '2026-03-01',
      state: 'Closed',
      graceEndsAt: null,
      reopenedUntil: null,
      isCurrent: false,
    },
  ],
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
            permissions: ['Project.Manage'],
            simulatedForUserId: null,
            userId: 1,
            userName: 'tester',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/api/v1/projects') && url.includes('/periods')) {
        return new Response(JSON.stringify(calendar), {
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
      <MemoryRouter initialEntries={['/admin/periods?projectId=7']}>
        <QueryClientProvider client={client}>
          <PeriodsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('PeriodsPage: попередження, коли поточний період не Open (lane2)', () => {
  it(
    'бейдж «current is not Open» стоїть РІВНО на поточному Closed-періоді',
    async () => {
      mockFetch();
      show();

      const currentRow = (await screen.findByText('202601', {}, { timeout: SlowEnvTimeout })).closest('tr');
      const otherRow = (await screen.findByText('202602', {}, { timeout: SlowEnvTimeout })).closest('tr');
      expect(currentRow).not.toBeNull();
      expect(otherRow).not.toBeNull();

      // ⛔ Мутаційний доказ: попередження — РІВНО в рядку поточного періоду.
      expect(
        currentRow?.textContent?.includes('⟦periods.currentNotOpen⟧'),
      ).toBe(true);

      // ⛔ І РІВНО НЕ в рядку не-поточного Closed-періоду — інакше
      // «попереджати про будь-який Closed» пройшло б так само.
      expect(
        otherRow?.textContent?.includes('⟦periods.currentNotOpen⟧'),
      ).toBe(false);
    },
    SlowEnvTimeout,
  );
});
