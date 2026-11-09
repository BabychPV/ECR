import { describe, it, expect, vi, afterEach } from 'vitest';
import { configure, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ProjectLifecycleTab } from '@/pages/projects/ProjectLifecycleTab';
import { testTheme } from '@/test/render';

/**
 * ✎ Projects (макет 08.11, рядки f6, c2–c4): вкладка Lifecycle — Activate, Clone, Archive з повною назвою, рядком
 * опису й ВИДИМОЮ причиною, коли дія недоступна. Перенесено з `ProjectSettingsPage.*` (X-25, X-29, Q-287, F-19):
 * важкі дії — через підтвердження, «Archive» — червона контурна, без гранта Manage кнопки обіцяли б 403.
 */

configure({ asyncUtilTimeout: 20_000 });

type Status = 'Draft' | 'Active' | 'Archived';

const period = (sequence: number, state: string) => ({
  id: sequence,
  periodKey: 202600 + sequence,
  year: 2026,
  sequence,
  state,
  startsAt: `2026-${String(sequence).padStart(2, '0')}-01T00:00:00+05:00`,
  endsAt: '2026-12-31T00:00:00+05:00',
  graceEndsAt: '2026-12-15T00:00:00+05:00',
  reopenedUntil: null,
  isCurrent: false,
});

function respond(
  status: Status,
  options: { grant?: 'Read' | 'Manage'; periods?: ReturnType<typeof period>[]; summaryDraft?: number } = {},
): { posts: string[] } {
  const state = { posts: [] as string[] };
  const periods = options.periods ?? [period(10, 'Open'), period(11, 'Open'), period(12, 'Scheduled')];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const json = (body: unknown, code = 200): Response =>
        new Response(JSON.stringify(body), { status: code, headers: { 'Content-Type': 'application/json' } });

      if (init?.method === 'DELETE') {
        state.posts.push(`DELETE ${url}`);
        return new Response(null, { status: 204 });
      }
      if (init?.method === 'POST') {
        state.posts.push(url);
        return json(url.endsWith('/clone') ? { projectId: 8 } : null);
      }
      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: { 'Project:7': options.grant ?? 'Manage' },
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Project.Manage', 'Period.Configure'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'admin',
        });
      }
      if (url.includes('/documents/summary')) {
        return json({ draft: options.summaryDraft ?? 92, partlySubmitted: 0, submitted: 0, approved: 0, rejected: 0, withIssues: 0 });
      }
      if (url.includes('/periods')) {
        return json({
          projectId: 7,
          periodKind: 'Monthly',
          currentPeriodMode: 'Auto',
          timeZoneId: 'Asia/Aqtau',
          policy: { id: 1, code: 'STD', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 45 },
          periods,
        });
      }
      if (url.includes('/api/v1/projects')) {
        return json({
          items: [{ id: 7, code: 'LANDUI2026', status, periodKind: 'Monthly', periodCount: 12, currentPeriodId: null, timeZoneId: 'Asia/Aqtau' }],
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
      <MemoryRouter initialEntries={['/projects/7/lifecycle']}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <ProjectLifecycleTab projectId={7} />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const reason = (id: string): string | null =>
  document.querySelector(`[data-lifecycle-reason="${id}"]`)?.textContent ?? null;

afterEach(() => {
  vi.unstubAllGlobals();
});

const Slow = 120_000;

describe('ProjectLifecycleTab: Delete draft (c1, c2)', () => {
  it(
    'c2: активний проєкт із документами — Delete недоступна, причина називає стан і кількість',
    async () => {
      respond('Active');
      show();

      const remove = await screen.findByRole('button', { name: '⟦projects.lifecycle.delete⟧' });
      await waitFor(() =>
        expect(reason('delete')).toBe('⟦projects.lifecycle.delete.notAllowed (status=⟦status.project.Active⟧, count=92)⟧'),
      );
      // ⛔ Мутація «deleteAllowed без перевірки статусу» робить тут кнопку активною.
      expect(remove.hasAttribute('disabled')).toBe(true);
    },
    Slow,
  );

  it(
    'c1: чернетка без документів — підтвердження лише після набору коду; DELETE іде один раз',
    async () => {
      const state = respond('Draft', { periods: [period(1, 'Scheduled')], summaryDraft: 0 });
      const user = userEvent.setup();
      show();

      const remove = await screen.findByRole('button', { name: '⟦projects.lifecycle.delete⟧' });
      await waitFor(() => expect(remove.hasAttribute('disabled')).toBe(false));
      await user.click(remove);

      const dialog = await screen.findByRole('dialog', { name: '⟦projects.delete.title (code=LANDUI2026)⟧' });
      const confirm = within(dialog).getByRole('button', { name: '⟦projects.delete.confirm⟧' });
      expect(confirm.hasAttribute('disabled')).toBe(true);
      await user.type(within(dialog).getByRole('textbox'), 'LANDUI202');
      // ⛔ Мутація «порівнювати без урахування останнього символу/регістру» вмикає кнопку тут.
      expect(confirm.hasAttribute('disabled')).toBe(true);
      expect(state.posts).toEqual([]);
      await user.type(within(dialog).getByRole('textbox'), '6');
      await waitFor(() => expect(confirm.hasAttribute('disabled')).toBe(false));
      await user.click(confirm);

      await waitFor(() => expect(state.posts).toEqual(['DELETE /api/v1/projects/7']));
    },
    Slow,
  );
});
describe('ProjectLifecycleTab: дії з видимими причинами (c2–c4)', () => {
  it(
    'c3: активний проєкт із відкритими місяцями — Archive недоступна, причина називає місяці',
    async () => {
      respond('Active');
      show();

      const archive = await screen.findByRole('button', { name: '⟦projects.lifecycle.archive⟧' });
      await waitFor(() => expect(reason('archive')).toBe('⟦projects.lifecycle.archive.openMonths (months=Oct, Nov 2026)⟧'));
      // ⛔ Мутація «кнопка активна, а причина лише в діалозі» дає тут enabled.
      expect(archive.hasAttribute('disabled')).toBe(true);
      // X-29: кінцева дія — червона контурна, а не сіра.
      expect(archive.getAttribute('data-variant')).toBe('outline');
      // c2-аналог для Activate: вже активний — причина видна.
      expect(reason('activate')).toBe('⟦projects.lifecycle.activate.already⟧');
    },
    Slow,
  );

  it(
    'усі місяці заблоковані — Archive доступна; підтвердження перед запитом (Q-287)',
    async () => {
      const state = respond('Active', { periods: [period(1, 'Closed'), period(2, 'Closed')] });
      const user = userEvent.setup();
      show();

      const archive = await screen.findByRole('button', { name: '⟦projects.lifecycle.archive⟧' });
      await waitFor(() => expect(archive.hasAttribute('disabled')).toBe(false));
      expect(reason('archive')).toBeNull();

      await user.click(archive);
      const dialog = await screen.findByRole('dialog', { name: '⟦periods.archive⟧' });
      expect(state.posts).toEqual([]);
      await user.click(within(dialog).getByRole('button', { name: '⟦periods.archive⟧' }));
      await waitFor(() => expect(state.posts).toEqual(['/api/v1/projects/7/archive']));
    },
    Slow,
  );

  it(
    'X-25: Activate чернетки — клік відкриває підтвердження, Cancel нічого не шле, підтвердження — шле',
    async () => {
      const state = respond('Draft', { periods: [period(1, 'Scheduled')], summaryDraft: 0 });
      const user = userEvent.setup();
      show();

      await user.click(await screen.findByRole('button', { name: '⟦projects.lifecycle.activate⟧' }));
      const dialog = await screen.findByRole('dialog', { name: '⟦periods.activateTitle (code=LANDUI2026)⟧' });
      expect(state.posts).toEqual([]);

      await user.click(within(dialog).getByRole('button', { name: '⟦common.cancel⟧' }));
      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
      expect(state.posts).toEqual([]);

      await user.click(screen.getByRole('button', { name: '⟦projects.lifecycle.activate⟧' }));
      const again = await screen.findByRole('dialog', { name: '⟦periods.activateTitle (code=LANDUI2026)⟧' });
      await user.click(within(again).getByRole('button', { name: '⟦periods.activate⟧' }));
      await waitFor(() => expect(state.posts).toEqual(['/api/v1/projects/7/activate']));

      // Чернетку архівувати не можна — причина названа видимо (c4).
      expect(reason('archive')).toBe('⟦projects.lifecycle.archive.notActive (status=⟦status.project.Draft⟧)⟧');
    },
    Slow,
  );

  it(
    'F-19: грант Read — дії на місці, але недоступні з причиною «немає права», а не зникають мовчки',
    async () => {
      respond('Active', { grant: 'Read', periods: [period(1, 'Closed')] });
      show();

      const clone = await screen.findByRole('button', { name: '⟦projects.lifecycle.clone⟧' });
      await waitFor(() => expect(reason('clone')).toBe('⟦projects.lifecycle.noRight⟧'));
      // ⛔ Мутація «лише право, без гранта» робить тут кнопки активними.
      expect(clone.hasAttribute('disabled')).toBe(true);
      expect(screen.getByRole('button', { name: '⟦projects.lifecycle.archive⟧' }).hasAttribute('disabled')).toBe(true);
    },
    Slow,
  );

  it(
    'c4: кожна дія має повну назву й рядок опису, пов’язаний через aria-describedby',
    async () => {
      respond('Active');
      show();

      for (const [name, description] of [
        ['⟦projects.lifecycle.activate⟧', '⟦projects.lifecycle.activate.desc⟧'],
        ['⟦projects.lifecycle.clone⟧', '⟦projects.lifecycle.clone.desc⟧'],
        ['⟦projects.lifecycle.archive⟧', '⟦projects.lifecycle.archive.desc⟧'],
        ['⟦projects.lifecycle.delete⟧', '⟦projects.lifecycle.delete.desc⟧'],
      ] as const) {
        const button = await screen.findByRole('button', { name });
        const describedBy = button.getAttribute('aria-describedby') ?? '';
        expect(describedBy, name).not.toBe('');
        expect(document.getElementById(describedBy)?.textContent, name).toContain(description);
      }
    },
    Slow,
  );
});
