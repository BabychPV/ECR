import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { testTheme } from '@/test/render';

/**
 * ✎ Period clarity (людина 07.11, п. a): сторінка Periods — лише блокування місяців. Рядок = місяць: підпис, стан
 * (Open / Late (flagged) / Locked) і ОДНА дата «Locks on»; ключ і порядковий номер — у підказці.
 *
 * ⚠ Підказки — `Hint` із `focusable` (Q-337): формула з РЕАЛЬНИХ чисел політики, відкривається фокусом, а не лише
 * наведенням. Каталог тут НЕ завантажується: `t()` повертає `⟦ключ (param=val)⟧`, тож доказ — у числах.
 */

const project = {
  id: 7,
  code: 'PRJ-7',
  status: 'Active' as const,
  periodKind: 'Monthly' as const,
  periodCount: 3,
  currentPeriodId: null,
  timeZoneId: 'Europe/Kyiv',
};

const calendar = {
  projectId: 7,
  periodKind: 'Monthly' as const,
  currentPeriodMode: 'Auto' as const,
  timeZoneId: 'Europe/Kyiv',
  policy: { id: 1, code: 'ECR-Standard', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 45 },
  periods: [
    { id: 1, periodKey: 202601, year: 2026, sequence: 1, startsAt: '2026-01-01T00:00:00+02:00', endsAt: '2026-03-17T00:00:00+02:00', state: 'Closed', graceEndsAt: '2026-02-15T00:00:00+02:00', reopenedUntil: null, isCurrent: false },
    { id: 2, periodKey: 202602, year: 2026, sequence: 2, startsAt: '2026-02-01T00:00:00+02:00', endsAt: '2026-04-15T00:00:00+03:00', state: 'Grace', graceEndsAt: '2026-03-15T00:00:00+02:00', reopenedUntil: null, isCurrent: false },
    { id: 3, periodKey: 202603, year: 2026, sequence: 3, startsAt: '2026-03-01T00:00:00+02:00', endsAt: '2026-05-15T00:00:00+03:00', state: 'Open', graceEndsAt: '2026-04-15T00:00:00+03:00', reopenedUntil: null, isCurrent: true },
  ],
};

/** POST-запити сторінки (UAT-p3: перерахунок проєкту). */
const posts: string[] = [];

function mockFetch(): void {
  posts.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (init?.method === 'POST') {
        posts.push(url);
        return json({ jobId: null, nothingToRecalculate: true, skippedSheets: [] });
      }

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: { 'Project:7': 'Manage' },
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Project.Manage', 'Period.Configure', 'Period.Reopen', 'Calculation.Recalculate'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (url.includes('/api/v1/projects') && url.includes('/periods')) return json(calendar);
      if (url.includes('/api/v1/projects')) return json({ items: [project], nextCursor: null, totalCount: 1 });

      return json(null);
    }),
  );
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

const Slow = 300_000;

/** Опис тригера з `aria-describedby` — те, що озвучить читач при фокусі. */
function description(el: HTMLElement): string {
  const ids = (el.getAttribute('aria-describedby') ?? '').split(' ').filter(Boolean);

  return ids.map((id) => document.getElementById(id)?.textContent ?? '').join(' ');
}

async function row(key: number): Promise<HTMLElement> {
  await screen.findByText((_, el) => el?.getAttribute('data-period-key') === String(key), {}, { timeout: 60_000 });

  return document.querySelector<HTMLElement>(`[data-period-row="${String(key)}"]`) as HTMLElement;
}

describe('PeriodsPage: рядок місяця — підпис, стан і одна дата блокування (Period clarity)', () => {
  it(
    'колонки — Month · State · Locks on · дії; без Range, Grace until, ключа й порядкового номера',
    async () => {
      mockFetch();
      show();
      await row(202601);

      const headers = screen.getAllByRole('columnheader').map((th) => th.textContent ?? '');
      expect(headers[0]).toContain('⟦periods.columnMonth⟧');
      expect(headers[1]).toContain('⟦periods.state⟧');
      expect(headers[2]).toContain('⟦periods.locksOn⟧');
      // ⛔ Мутація «повернути колонки Range/Grace/Key/Sequence» дає тут зайві заголовки.
      expect(headers).toHaveLength(4);
      expect(headers.join(' ')).not.toMatch(/periods\.(range|grace|key|sequence)⟧/);

      // Пояснення сторінки — видно одразу.
      expect(screen.getByText('⟦periods.lockOnlyNote⟧')).toBeTruthy();
      // ✎ D-468: погоджень перерахунку закритого періоду на сторінці більше немає.
      expect(document.body.textContent).not.toMatch(/recalcApproval|closedPeriodRecalc/i);
    },
    Slow,
  );

  it(
    'п. b: маршрут, політики, клон, пояс і архів — не тут; є посилання на налаштування проєкту',
    async () => {
      mockFetch();
      show();
      await row(202601);

      // ⛔ Мутація «повернути дії проєкту на сторінку» (права й грант Manage у фікстурі є) дає тут кнопки.
      for (const name of [
        '⟦workflow.route⟧',
        '⟦periods.policies⟧',
        '⟦periods.clone⟧',
        '⟦periods.timezoneChange⟧',
        '⟦periods.archive⟧',
        '⟦periods.create⟧',
        '⟦workflow.recalculate⟧',
      ]) {
        expect(screen.queryByRole('button', { name })).toBeNull();
      }

      const link = screen.getByRole('link', { name: '⟦projectSettings.title⟧' });
      expect(link.getAttribute('href')).toBe('/admin/projects?projectId=7');
    },
    Slow,
  );

  it(
    'Periods offers Recalculate project for the selected active project, confirmed before any request',
    async () => {
      // ✎ UAT-p3: людина шукала перерахунок тут — та сама дія, що на переліку документів (ProjectRecalculateAction).
      mockFetch();
      const user = userEvent.setup();
      show();
      await row(202601);

      await user.click(await screen.findByRole('button', { name: '⟦periods.recalcProject⟧' }));
      const dialog = await screen.findByRole('dialog', { name: '⟦periods.recalcTitle (code=PRJ-7)⟧' });
      expect(posts).toEqual([]);

      await user.click(within(dialog).getByRole('button', { name: '⟦workflow.recalculate⟧' }));
      await waitFor(() => expect(posts).toEqual(['/api/v1/projects/7/recalculate']));
    },
    Slow,
  );

  it(
    'підпис місяця — назва; ключ і номер — у підказці, доступній з клавіатури',
    async () => {
      mockFetch();
      show();
      const march = await row(202603);

      const caption = within(march).getByText('March 2026');
      expect(caption.getAttribute('data-period-key')).toBe('202603');
      // ⛔ Ключ не текстом рядка: мутація «повернути `{period.periodKey}`» дає «202603» у видимому тексті.
      expect(within(march).queryByText('202603')).toBeNull();

      expect(caption.tabIndex).toBe(0);
      expect(description(caption)).toContain('⟦periods.keyTooltip (key=202603, sequence=3)⟧');

      caption.focus();
      expect((await screen.findByRole('tooltip')).textContent).toContain('key=202603');
    },
    Slow,
  );

  it(
    'стан кожного місяця — бейдж; «Locks on» — одна дата, а заголовок пояснює формулу числами політики',
    async () => {
      mockFetch();
      show();

      const jan = await row(202601);
      const feb = await row(202602);
      const mar = await row(202603);

      expect(within(jan).getByText('⟦status.period.Closed⟧')).toBeTruthy();
      expect(within(feb).getByText('⟦status.period.Grace⟧')).toBeTruthy();
      expect(within(mar).getByText('⟦status.period.Open⟧')).toBeTruthy();

      for (const tr of [jan, feb, mar]) {
        expect(tr.querySelectorAll('time')).toHaveLength(1);
      }

      expect(feb.querySelector('time')?.getAttribute('dateTime')).toBe('2026-04-15T00:00:00+03:00');

      const header = within(screen.getByRole('columnheader', { name: /periods\.locksOn(?!Hint)/ })).getByText(
        '⟦periods.locksOn⟧',
      );
      expect(header.tabIndex).toBe(0);
      const text = description(header);
      expect(text).toContain('grace=15');
      expect(text).toContain('hardClose=45');
      expect(text).toContain('code=ECR-Standard');
    },
    Slow,
  );
});
