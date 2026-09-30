import { describe, it, expect, vi, afterEach } from 'vitest';
import { configure, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { testTheme } from '@/test/render';

/**
 * `X-25`: «Activate project» (незворотне `Draft → Active`) і перерахунок
 * УСЬОГО проєкту йшли одним кліком. `X-29`: «Archive» з
 * `variant="default"` + `color="statusError"` виглядав сірим, як «Clone».
 *
 * ⛔ Доказ — не «діалог з'явився», а що клік по кнопці САМ ПО СОБІ не
 * надсилає запиту, а «Cancel» у діалозі не надсилає його й потім.
 */

// ⚠ Повільне середовище (усі тести PeriodsPage паралельно): типова стеля
// indBy* в 1 с тут червоніє не з тієї причини, яку тест стереже.
configure({ asyncUtilTimeout: 20_000 });

type Status = 'Draft' | 'Active';

function respond(status: Status, grant: 'Read' | 'Manage' = 'Manage'): { posts: string[] } {
  const state = { posts: [] as string[] };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const json = (body: unknown, code = 200): Response =>
        new Response(JSON.stringify(body), { status: code, headers: { 'Content-Type': 'application/json' } });

      if (init?.method === 'POST') {
        state.posts.push(url);

        return url.endsWith('/recalculate') ? json({ jobId: 'IRecalculationJob-1' }, 202) : json(null);
      }

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          // F-19: дії над конкретним проєктом вимагають гранта Manage на нього.
          grants: { 'Project:7': grant },
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
          items: [{ id: 7, code: 'PRJ-7', status, periodKind: 'Monthly', periodCount: 0, currentPeriodId: null, timeZoneId: 'Asia/Aqtau' }],
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

afterEach(() => {
  vi.unstubAllGlobals();
});

const Slow = 120_000;

describe('PeriodsPage: важкі дії проєкту — через підтвердження', () => {
  it(
    'Activate: клік відкриває підтвердження, Cancel нічого не шле, підтвердження — шле',
    async () => {
      const state = respond('Draft');
      const user = userEvent.setup();
      show();

      await user.click(await screen.findByRole('button', { name: '⟦periods.activate⟧' }));

      // ⛔ Мутація «`activate.mutate` прямо на кнопці» шле POST уже тут.
      const dialog = await screen.findByRole('dialog', { name: '⟦periods.activateTitle (code=PRJ-7)⟧' });
      expect(state.posts).toEqual([]);

      // Фокус — на безпечній дії (`L6`).
      expect(document.activeElement).toBe(within(dialog).getByRole('button', { name: '⟦common.cancel⟧' }));

      await user.click(within(dialog).getByRole('button', { name: '⟦common.cancel⟧' }));
      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
      expect(state.posts).toEqual([]);

      await user.click(screen.getByRole('button', { name: '⟦periods.activate⟧' }));
      const again = await screen.findByRole('dialog', { name: '⟦periods.activateTitle (code=PRJ-7)⟧' });
      await user.click(within(again).getByRole('button', { name: '⟦periods.activate⟧' }));

      await waitFor(() => expect(state.posts).toEqual(['/api/v1/projects/7/activate']));
    },
    Slow,
  );

  it(
    'перерахунок усього проєкту: клік відкриває підтвердження, запит — лише після нього',
    async () => {
      const state = respond('Active');
      const user = userEvent.setup();
      show();

      await user.click(await screen.findByRole('button', { name: '⟦workflow.recalculate⟧' }));

      const dialog = await screen.findByRole('dialog', { name: '⟦periods.recalcTitle (code=PRJ-7)⟧' });
      expect(state.posts).toEqual([]);

      await user.click(within(dialog).getByRole('button', { name: '⟦workflow.recalculate⟧' }));

      await waitFor(() => expect(state.posts).toEqual(['/api/v1/projects/7/recalculate']));
    },
    Slow,
  );

  it(
    'X-29: «Archive» — червона контурна, а не сіра `default`',
    async () => {
      respond('Active');
      show();

      const archive = await screen.findByRole('button', { name: '⟦periods.archive⟧' });

      // ⛔ Мутація «повернути `variant="default"`» дає тут `default`.
      expect(archive.getAttribute('data-variant')).toBe('outline');
    },
    Slow,
  );

  it(
    'X-29: колонка дій календаря має назву для читалки',
    async () => {
      respond('Active');
      show();

      expect(await screen.findByRole('columnheader', { name: '⟦common.actions⟧' })).toBeTruthy();
    },
    Slow,
  );
});

describe('PeriodsPage: дії над проєктом — лише з грантом Manage на нього (F-19)', () => {
  it(
    'грант Read: «Make current», «Archive», «Clone» не пропонуються — сервер дав би 403',
    async () => {
      respond('Active', 'Read');
      show();

      // Календар доїхав — рядок періоду на екрані.
      await screen.findByText('202601');

      // ⛔ Мутація «лише право, без гранта» (стара умова) показує тут
      // «Make current» на рядку й «Archive»/«Clone» у шапці.
      expect(screen.queryByRole('button', { name: '⟦periods.pin⟧' })).toBeNull();
      expect(screen.queryByRole('button', { name: '⟦periods.archive⟧' })).toBeNull();
      expect(screen.queryByRole('button', { name: '⟦periods.clone⟧' })).toBeNull();
    },
    Slow,
  );

  it(
    'грант Manage: ті самі дії на місці',
    async () => {
      respond('Active', 'Manage');
      show();

      expect(await screen.findByRole('button', { name: '⟦periods.pin⟧' })).toBeTruthy();
      expect(screen.getByRole('button', { name: '⟦periods.archive⟧' })).toBeTruthy();
    },
    Slow,
  );
});