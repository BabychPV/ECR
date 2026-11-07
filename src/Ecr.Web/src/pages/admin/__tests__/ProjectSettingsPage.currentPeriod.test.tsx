import { describe, it, expect, vi, afterEach } from 'vitest';
import { configure, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ProjectSettingsPage } from '@/pages/admin/ProjectSettingsPage';
import { testTheme } from '@/test/render';

/**
 * ✎ Period clarity (людина 07.11, п. c): поточний період — у налаштуваннях проєкту з видимим режимом
 * («Automatic by date» / «Pinned to <місяць>»), «Pin…» і «Back to automatic» — через нові ендпоінти
 * `…/settings/current-period` (`PUT` / `DELETE …/pin`). Старий `PUT /projects/{id}/current-period` не кличеться.
 */

configure({ asyncUtilTimeout: 30_000 });

interface Call {
  method: string;
  url: string;
  body: unknown;
}

function respond(mode: 'Auto' | 'Pinned', grant: 'Manage' | 'Read' = 'Manage'): { calls: Call[] } {
  const state = { calls: [] as Call[], mode };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
      const setting = (): unknown => ({
        projectId: 7,
        mode: state.mode,
        currentPeriodId: state.mode === 'Pinned' ? 1 : 3,
        currentPeriodKey: state.mode === 'Pinned' ? 202601 : 202603,
        automaticPeriodId: 3,
        automaticPeriodKey: 202603,
        pinnedReason: state.mode === 'Pinned' ? 'Year-end catch-up' : null,
        changedAt: null,
      });

      if (method !== 'GET') {
        state.calls.push({ method, url, body: init?.body === undefined ? null : JSON.parse(String(init.body)) });
        if (url.endsWith('/settings/current-period/pin')) state.mode = 'Auto';
        if (url.endsWith('/settings/current-period')) state.mode = 'Pinned';
        return json(setting());
      }

      if (url.includes('/settings/current-period')) return json(setting());

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: { 'Project:7': grant },
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Period.Configure', 'Project.Manage'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'admin',
        });
      }

      if (url.includes('/periods')) {
        return json({
          projectId: 7,
          periodKind: 'Monthly',
          currentPeriodMode: state.mode,
          timeZoneId: 'Asia/Aqtau',
          policy: { id: 1, code: 'ECR-Standard', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 45 },
          periods: [1, 2, 3].map((n) => ({
            id: n,
            periodKey: 202600 + n,
            year: 2026,
            sequence: n,
            state: 'Open',
            startsAt: '2026-01-01T00:00:00+05:00',
            endsAt: '2026-03-17T00:00:00+05:00',
            graceEndsAt: '2026-02-15T00:00:00+05:00',
            reopenedUntil: null,
            isCurrent: n === 3,
          })),
        });
      }

      if (url.includes('/api/v1/projects')) {
        return json({
          items: [{ id: 7, code: 'PRJ-7', status: 'Active', periodKind: 'Monthly', periodCount: 3, currentPeriodId: 3, timeZoneId: 'Asia/Aqtau' }],
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
      <MemoryRouter initialEntries={['/admin/projects?projectId=7']}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <ProjectSettingsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const Slow = 300_000;

async function section(): Promise<HTMLElement> {
  await screen.findByText((_, el) => el?.getAttribute('data-current-period-badge') !== null && el?.tagName !== 'SCRIPT');

  return document.querySelector<HTMLElement>('[data-project-section="current-period"]') as HTMLElement;
}

describe('ProjectSettingsPage: поточний період — режим, «Pin…» і «Back to automatic» (Period clarity п. c)', () => {
  it(
    'Auto: видно режим «Automatic by date»; «Pin…» шле PUT нових налаштувань з періодом і причиною',
    async () => {
      const state = respond('Auto');
      const user = userEvent.setup();
      show();

      const box = await section();
      expect(within(box).getByText('⟦projectSettings.modeAuto⟧')).toBeTruthy();
      expect(within(box).getByText('⟦projectSettings.currentIs (period=March 2026)⟧', { exact: false })).toBeTruthy();
      expect(within(box).queryByRole('button', { name: '⟦projectSettings.backToAuto⟧' })).toBeNull();

      await user.click(within(box).getByRole('button', { name: '⟦projectSettings.pin⟧' }));
      const dialog = await screen.findByRole('dialog', { name: '⟦periods.pin⟧' });
      const confirm = within(dialog).getByRole('button', { name: '⟦periods.pin⟧' });
      // ⛔ Без періоду й причини — не шлемо.
      expect((confirm as HTMLButtonElement).disabled).toBe(true);

      await user.click(within(dialog).getByRole('textbox', { name: /projectSettings\.pinPeriod/ }));
      await user.click(await screen.findByRole('option', { name: /January 2026/ }));
      await user.type(within(dialog).getByRole('textbox', { name: /workflow\.reason/ }), 'Year-end catch-up');
      await user.click(confirm);

      await waitFor(() =>
        expect(state.calls).toEqual([
          {
            method: 'PUT',
            url: '/api/v1/projects/7/settings/current-period',
            body: { mode: 'Pinned', pinnedPeriodId: 1, reason: 'Year-end catch-up' },
          },
        ]),
      );
      // ⛔ Старий маршрут (`PUT /projects/{id}/current-period`) — не кличеться.
      expect(state.calls.some((c) => c.url.endsWith('/7/current-period'))).toBe(false);

      expect(await within(box).findByText('⟦projectSettings.modePinned (period=January 2026)⟧')).toBeTruthy();
    },
    Slow,
  );

  it(
    'Pinned: видно «Pinned to <місяць>» і причину; «Back to automatic» шле DELETE …/pin і повертає Auto',
    async () => {
      const state = respond('Pinned');
      const user = userEvent.setup();
      show();

      const box = await section();
      expect(within(box).getByText('⟦projectSettings.modePinned (period=January 2026)⟧')).toBeTruthy();
      expect(within(box).getByText('⟦projectSettings.pinnedReason (reason=Year-end catch-up)⟧')).toBeTruthy();
      expect(within(box).getByText('⟦projectSettings.automaticWouldBe (period=March 2026)⟧')).toBeTruthy();

      await user.click(within(box).getByRole('button', { name: '⟦projectSettings.backToAuto⟧' }));

      await waitFor(() =>
        expect(state.calls).toEqual([
          { method: 'DELETE', url: '/api/v1/projects/7/settings/current-period/pin', body: null },
        ]),
      );
      expect(await within(box).findByText('⟦projectSettings.modeAuto⟧')).toBeTruthy();
    },
    Slow,
  );

  it(
    'грант Read: режим видно, дій немає (F-19)',
    async () => {
      respond('Pinned', 'Read');
      show();

      const box = await section();
      expect(within(box).getByText('⟦projectSettings.modePinned (period=January 2026)⟧')).toBeTruthy();
      expect(within(box).queryByRole('button', { name: '⟦projectSettings.pin⟧' })).toBeNull();
      expect(within(box).queryByRole('button', { name: '⟦projectSettings.backToAuto⟧' })).toBeNull();
    },
    Slow,
  );
});
