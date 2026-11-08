import type { JSX } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { configure, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { LegacyProjectRedirect } from '@/app/projectRoutes';
import { ProjectsPage } from '@/pages/projects/ProjectsPage';
import { testTheme } from '@/test/render';

/**
 * ✎ Projects (макет 08.11): перелік проєктів (рядок f3) і перенаправлення старих адрес (f2).
 * Стани переліку (відмова/порожньо/у дорозі) перенесено з колишнього `PeriodsPage.states` — перелік проєктів тепер тут.
 */

configure({ asyncUtilTimeout: 20_000 });

const period = (sequence: number, state: string) => ({
  id: sequence,
  periodKey: 202600 + sequence,
  year: 2026,
  sequence,
  state,
  startsAt: '2026-01-01T00:00:00+05:00',
  endsAt: `2026-12-${String(sequence + 10)}T00:00:00+05:00`,
  graceEndsAt: '2026-12-01T00:00:00+05:00',
  reopenedUntil: null,
  isCurrent: sequence === 11,
});

const json = (body: unknown, code = 200): Response =>
  new Response(JSON.stringify(body), { status: code, headers: { 'Content-Type': 'application/json' } });

function serve(projects: () => Promise<Response> | Response): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/v1/me')) {
        return json({
          denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false,
          permissions: ['Project.Manage', 'Period.Configure'], simulatedForUserId: null, userId: 1, userName: 'admin',
        });
      }
      if (/\/api\/v1\/projects\/\d+\/periods/.test(url)) {
        const id = Number(/projects\/(\d+)/.exec(url)?.[1]);
        return json({
          projectId: id, periodKind: 'Monthly', currentPeriodMode: 'Auto', timeZoneId: 'Asia/Aqtau',
          policy: { id: 1, code: 'STD', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 30, yearGraceOffsetDays: 0 },
          periods: id === 1 ? [period(9, 'Closed'), period(10, 'Open'), period(11, 'Open'), period(12, 'Scheduled')] : [period(1, 'Scheduled')],
        });
      }
      if (url.includes('/api/v1/projects')) return projects();
      return json(null);
    }),
  );
}

const listed = (): Response =>
  json({
    items: [
      { id: 1, code: 'LANDUI2026', status: 'Active', periodKind: 'Monthly', periodCount: 12, currentPeriodId: 11, timeZoneId: 'Asia/Aqtau' },
      { id: 2, code: 'Pavlo_TEST', status: 'Draft', periodKind: 'Monthly', periodCount: 12, currentPeriodId: null, timeZoneId: 'Asia/Aqtau' },
    ],
    nextCursor: null,
    totalCount: 2,
  });

function Where(): JSX.Element {
  return <output data-testid="where">{useLocation().pathname}</output>;
}

function show(entry = '/projects'): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[entry]}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Routes>
            <Route path="/projects" element={<ProjectsPage />} />
            <Route path="/admin/periods" element={<LegacyProjectRedirect tab="periods" />} />
            <Route path="/admin/projects" element={<LegacyProjectRedirect tab="settings" />} />
            <Route path="*" element={<Where />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ProjectsPage (f3)', () => {
  it('рядок: код, статус, поточний період, стан місяців і найближче блокування; «New project» є', async () => {
    serve(listed);
    show();

    const link = await screen.findByRole('link', { name: 'LANDUI2026' });
    expect(link.getAttribute('href')).toBe('/projects/1/periods');
    const row = link.closest('tr') as HTMLElement;
    await waitFor(() => expect(row.textContent).toContain('projects.state.open (months=Oct, Nov 2026)'));
    expect(row.textContent).toContain('status.project.Active');
    expect(row.textContent).toContain('projects.closes.locks');

    const draft = (await screen.findByRole('link', { name: 'Pavlo_TEST' })).closest('tr') as HTMLElement;
    expect(within(draft).getByText('⟦projects.state.notStarted⟧')).toBeTruthy();
    expect(draft.textContent).toContain('projects.closes.afterActivation');

    expect(await screen.findByRole('button', { name: '⟦projects.create⟧' })).toBeTruthy();
  });

  it('клац по рядку відкриває проєкт на вкладці Periods', async () => {
    serve(listed);
    const user = userEvent.setup();
    show();

    const row = (await screen.findByRole('link', { name: 'Pavlo_TEST' })).closest('tr') as HTMLElement;
    await user.click(within(row).getByText('⟦status.project.Draft⟧'));
    expect((await screen.findByTestId('where')).textContent).toBe('/projects/2/periods');
  });

  it('500: помилка з кодом, а не «проєктів немає»', async () => {
    serve(() =>
      new Response(JSON.stringify({ title: 'Server error', status: 500, errorCode: 'ECR-SYS-0500', correlationId: 'c', detail: null }), {
        status: 500,
        headers: { 'Content-Type': 'application/problem+json' },
      }),
    );
    show();

    expect(await screen.findByText('ECR-SYS-0500', { exact: false })).toBeTruthy();
    expect(screen.queryByText('⟦periods.noProjects⟧')).toBeNull();
  });

  it('порожній перелік: «проєктів немає»', async () => {
    serve(() => json({ items: [], nextCursor: null, totalCount: 0 }));
    show();

    expect(await screen.findByText('⟦periods.noProjects⟧')).toBeTruthy();
  });
});

describe('Старі адреси (f2)', () => {
  it.each([
    ['/admin/periods', '/projects'],
    ['/admin/projects', '/projects'],
    ['/admin/periods?projectId=7', '/projects/7/periods'],
    ['/admin/projects?projectId=7', '/projects/7/settings'],
    ['/admin/projects?projectId=abc', '/projects'],
  ])('%s → %s', async (from, to) => {
    serve(listed);
    show(from);

    if (to === '/projects') expect(await screen.findByRole('link', { name: 'LANDUI2026' })).toBeTruthy();
    else expect((await screen.findByTestId('where')).textContent).toBe(to);
  });
});
