import { describe, it, expect, vi, afterEach } from 'vitest';
import { configure, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { testTheme } from '@/test/render';

/**
 * Перерахунок усього проєкту (P4 ФВ-9.8): батьківська задача лише РОЗКЛАДАЄ
 * документні задачі й одразу стає `Succeeded`. Екран читав це як «перерахунок
 * завершено» — над жодним ще не порахованим документом.
 *
 * ⛔ Доказ: поки `effectiveState = FannedOut`, тосту «завершено» немає, рядок
 * «розкладено N, виконано M з N, помилок K» на екрані й опитування триває; тост
 * «завершено» — лише коли M = N і K = 0; з помилками — окреме попередження.
 */

vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn(), hide: vi.fn() } }));

// ⚠ Повільне середовище (усі тести PeriodsPage паралельно).
configure({ asyncUtilTimeout: 20_000 });

interface Fan {
  total: number;
  queued: number;
  running: number;
  succeeded: number;
  failed: number;
}

/** Відповіді сервера; `job` — те, що віддає `GET /jobs/{id}` зараз. */
function respond(): { job: { effectiveState: string; fanOut: Fan }; jobReads: number } {
  const state = {
    job: { effectiveState: 'FannedOut', fanOut: { total: 3, queued: 2, running: 0, succeeded: 1, failed: 0 } },
    jobReads: 0,
  };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const json = (body: unknown, code = 200): Response =>
        new Response(JSON.stringify(body), { status: code, headers: { 'Content-Type': 'application/json' } });

      if (init?.method === 'POST') {
        return url.endsWith('/recalculate') ? json({ jobId: 'IRecalculationJob-1' }, 202) : json(null);
      }

      if (url.includes('/api/v1/jobs/')) {
        state.jobReads += 1;

        return json({
          jobId: 'IRecalculationJob-1',
          state: 'Succeeded',
          percent: 100,
          message: null,
          error: null,
          errorCode: null,
          ...state.job,
        });
      }

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: { 'Project:7': 'Manage' },
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Project.Manage', 'Calculation.Recalculate', 'Period.Configure'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'admin',
        });
      }

      if (url.includes('/periods')) {
        return json({
          projectId: 7,
          periodKind: 'Monthly',
          currentPeriodMode: 'Auto',
          timeZoneId: 'Asia/Aqtau',
          policy: { id: 1, code: 'ECR-Standard', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 45 },
          periods: [
            { id: 1, periodKey: 202601, year: 2026, sequence: 1, state: 'Scheduled', startsAt: '2026-01-01T00:00:00+05:00', endsAt: '2026-03-17T00:00:00+05:00', graceEndsAt: '2026-02-15T00:00:00+05:00', reopenedUntil: null, isCurrent: false },
          ],
        });
      }

      if (url.includes('/api/v1/projects')) {
        return json({
          items: [{ id: 7, code: 'PRJ-7', status: 'Active', periodKind: 'Monthly', periodCount: 0, currentPeriodId: null, timeZoneId: 'Asia/Aqtau' }],
          nextCursor: null,
          totalCount: 1,
        });
      }

      return json(null);
    }),
  );

  return state;
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/periods?projectId=7']}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <PeriodsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/** Тексти всіх показаних сповіщень. */
function shown(): string[] {
  return vi.mocked(notifications.show).mock.calls.map(([n]) => String(n.message));
}

async function startRecalc(): Promise<void> {
  const user = userEvent.setup();

  await user.click(await screen.findByRole('button', { name: '⟦workflow.recalculate⟧' }));
  const dialog = await screen.findByRole('dialog', { name: '⟦periods.recalcTitle (code=PRJ-7)⟧' });
  await user.click(within(dialog).getByRole('button', { name: '⟦workflow.recalculate⟧' }));
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(notifications.show).mockClear();
});

const Slow = 120_000;
const Done = '⟦workflow.recalcDone⟧';

describe('PeriodsPage: правдивий стан перерахунку проєкту (P4 ФВ-9.8)', () => {
  it(
    'розкладено, документи ще рахуються — не «завершено», прогрес на екрані, опитування триває; M = N, K = 0 — «завершено»',
    async () => {
      const state = respond();
      show();

      await startRecalc();

      expect(
        await screen.findByText('⟦jobs.fanOutProgress (total=3, done=1, failed=0)⟧'),
      ).toBeTruthy();

      // ⛔ Мутація «`outcomeOf` без `effectiveState`» показує «завершено» вже тут.
      // ⛔ Мутація «`pollInterval` без `effectiveState`» зупиняє опитування на першому читанні.
      await waitFor(() => expect(state.jobReads).toBeGreaterThanOrEqual(2));
      expect(shown()).not.toContain(Done);
      expect(screen.getByRole('button', { name: '⟦workflow.recalcRunning⟧' })).toBeTruthy();

      state.job = { effectiveState: 'Succeeded', fanOut: { total: 3, queued: 0, running: 0, succeeded: 3, failed: 0 } };

      await waitFor(() => expect(shown()).toContain(Done));
      expect(await screen.findByText('⟦jobs.fanOutProgress (total=3, done=3, failed=0)⟧')).toBeTruthy();
      expect(shown().filter((m) => m === Done)).toHaveLength(1);
    },
    Slow,
  );

  it(
    'усі завершились, частина з помилками — попередження з K, а не «завершено»',
    async () => {
      const state = respond();
      state.job = { effectiveState: 'SucceededWithErrors', fanOut: { total: 3, queued: 0, running: 0, succeeded: 2, failed: 1 } };
      show();

      await startRecalc();

      const partial = '⟦workflow.recalcDoneWithErrors (total=3, done=2, failed=1)⟧';

      // ⛔ Мутація «`partial` як `succeeded`» дає тут «завершено».
      await waitFor(() => expect(shown()).toContain(partial));
      expect(shown()).not.toContain(Done);
      expect(vi.mocked(notifications.show).mock.calls.find(([n]) => n.message === partial)?.[0].color).toBe(
        'statusWarning',
      );
    },
    Slow,
  );
});
