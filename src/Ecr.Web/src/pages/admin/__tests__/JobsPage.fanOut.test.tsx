import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { JobsPage } from '@/pages/admin/JobsPage';
import { testTheme } from '@/test/render';

/**
 * P4 ФВ-9.8: батько-розклад перерахунку лишається `Succeeded`, а екран показує
 * похідний стан дочірніх — інакше оператор читає «виконано» на непорахованому.
 *
 * ⚠ Перевіряється атрибут `data-job-fanout` (похідний стан), а не текст: текст іде
 * з каталогу, якого в тесті немає. Мутація: `FanOutSummary` повертає `null` —
 * обидва тести червоні.
 */

const Session = {
  denies: [],
  grants: {},
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: [],
  simulatedForUserId: null,
  userId: 1,
  userName: 'tester',
};

const SlowEnvTimeout = 400_000;

function respond(status: unknown): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const body = url.includes('/api/v1/me')
        ? Session
        : url.includes('/api/v1/jobs/IRecalculationJob-p')
          ? status
          : [];

      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/jobs?id=IRecalculationJob-p1']}>
        <QueryClientProvider client={client}>
          <JobsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const Parent = {
  jobId: 'IRecalculationJob-p1',
  state: 'Succeeded',
  percent: 100,
  message: null,
  error: null,
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('JobsPage: розклад на дочірні задачі', () => {
  it(
    'Розкладено, поки дочірні не завершились',
    async () => {
      respond({
        ...Parent,
        effectiveState: 'FannedOut',
        fanOut: { total: 3, queued: 2, running: 0, succeeded: 1, failed: 0 },
      });
      show();

      const node = await screen.findByText(
        (_text, element) => element?.getAttribute('data-job-fanout') === 'FannedOut',
        {},
        { timeout: SlowEnvTimeout },
      );

      expect(node).toBeTruthy();
    },
    SlowEnvTimeout,
  );

  it(
    'Виконано з помилками показує стан дочірніх',
    async () => {
      respond({
        ...Parent,
        effectiveState: 'SucceededWithErrors',
        fanOut: { total: 3, queued: 0, running: 0, succeeded: 2, failed: 1 },
      });
      show();

      await screen.findByText(
        (_text, element) => element?.getAttribute('data-job-fanout') === 'SucceededWithErrors',
        {},
        { timeout: SlowEnvTimeout },
      );
    },
    SlowEnvTimeout,
  );

  it(
    'бейдж стану — похідний стан розкладу, а не «Succeeded» батька',
    async () => {
      respond({
        ...Parent,
        effectiveState: 'FannedOut',
        fanOut: { total: 3, queued: 2, running: 0, succeeded: 1, failed: 0 },
      });
      show();

      // ⛔ Мутація «бейдж за `status.state`» показує тут «Succeeded» над непорахованими документами.
      expect(await screen.findByText('⟦status.job.FannedOut⟧', {}, { timeout: SlowEnvTimeout })).toBeTruthy();
      expect(screen.queryByText('⟦status.job.Succeeded⟧')).toBeNull();
    },
    SlowEnvTimeout,
  );
});
