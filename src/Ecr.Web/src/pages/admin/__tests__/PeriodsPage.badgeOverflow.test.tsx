import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';

/**
 * Аудит-пас 5: `Badge` стану періоду (`periods.state`) обтинався еліпсисом,
 * щойно сторінка звужувалась — Mantine `Badge .label` (`overflow: hidden;
 * text-overflow: ellipsis`) реагує на будь-яке звуження предка. `SecurityPage.tsx`
 * уже впіймала й виправила той самий дефект для матриці ролей, обгорнувши
 * `<Table>` у `<ScrollArea>`: контейнер прокрутки бере на себе звуження
 * замість самої таблиці/бейджа.
 *
 * ⛔ Мутаційний доказ структурний, не візуальний: jsdom не рахує layout, тому
 * тест не може виміряти сам ellipsis. Натомість перевіряється те, що ЛІКУЄ
 * дефект, — таблиця справді всередині `ScrollArea` (Mantine позначає його
 * стабільним класом `mantine-ScrollArea-root`, не лише хешем), а не голою
 * `<div>`/сторінкою.
 */
const project = {
  id: 7,
  code: 'PRJ-7',
  status: 'Active' as const,
  periodKind: 'Monthly' as const,
  periodCount: 1,
  currentPeriodId: null,
  timeZoneId: 'Europe/Kyiv',
};

const calendar = {
  projectId: 7,
  periodKind: 'Monthly' as const,
  currentPeriodMode: 'Auto' as const,
  timeZoneId: 'Europe/Kyiv',
  periods: [
    {
      id: 1,
      periodKey: 202601,
      sequence: 1,
      startsAt: '2026-01-01',
      endsAt: '2026-02-01',
      state: 'Open',
      graceEndsAt: null,
      reopenedUntil: null,
      isCurrent: true,
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
    <MantineProvider>
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

describe('PeriodsPage: таблиця періодів у ScrollArea (аудит-пас 5)', () => {
  it(
    'таблиця з бейджем стану — всередині ScrollArea, а не голою на сторінці',
    async () => {
      mockFetch();
      show();

      const table = await screen.findByRole('table', {}, { timeout: SlowEnvTimeout });
      const scrollArea = table.closest('.mantine-ScrollArea-root');

      expect(scrollArea).not.toBeNull();
    },
    SlowEnvTimeout,
  );
});
