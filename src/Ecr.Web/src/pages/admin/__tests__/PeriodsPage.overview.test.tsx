import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { loadCatalog } from '@/shared/i18n';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { daysLeft, periodNote } from '@/features/projects/PeriodsOverview';
import type { PeriodCalendarDto } from '@/api/types';
import { testTheme } from '@/test/render';

/**
 * UI-33 — Periods за макетом (`docs/design/hybrid/screens-ops.js`,
 * `/admin/periods`): без вибору проєкту — таблиця «All projects · current
 * period» зі смугою показників; обраний проєкт — плитки періодів року.
 *
 * Приймання з картки: 1) без вибору — таблиця всіх проєктів із поточним
 * періодом; 2) вибір проєкту показує плитки зі станом і датами; 3) плитки не
 * несуть дій «Open/Close» (рішення 1: лише «Reopen» із причиною в календарі).
 */

const DayMs = 24 * 60 * 60 * 1000;
const now = Date.now();
const iso = (offsetDays: number): string => new Date(now + offsetDays * DayMs).toISOString();

const projects = [
  { id: 1, code: 'ALPHA', status: 'Active', periodKind: 'Monthly', timeZoneId: 'UTC', currentPeriodId: 11, periodCount: 3 },
  { id: 2, code: 'BETA', status: 'Active', periodKind: 'Monthly', timeZoneId: 'UTC', currentPeriodId: 21, periodCount: 2 },
];

const policy = { id: 1, code: 'STD', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 0 };

function period(id: number, sequence: number, state: string, isCurrent: boolean, endsInDays: number) {
  return {
    id,
    periodKey: 202600 + sequence,
    year: 2026,
    sequence,
    state,
    isCurrent,
    startsAt: `2026-${String(sequence).padStart(2, '0')}-01T00:00:00Z`,
    endsAt: iso(endsInDays + 0.5),
    graceEndsAt: null,
    reopenedUntil: null,
  };
}

const calendars: Record<number, PeriodCalendarDto> = {
  1: {
    projectId: 1,
    timeZoneId: 'UTC',
    periodKind: 'Monthly',
    currentPeriodMode: 'Auto',
    policy,
    periods: [period(10, 8, 'Closed', false, -30), period(11, 9, 'Open', true, 20), period(12, 10, 'Scheduled', false, 60)],
  } as unknown as PeriodCalendarDto,
  2: {
    projectId: 2,
    timeZoneId: 'UTC',
    periodKind: 'Monthly',
    currentPeriodMode: 'Auto',
    policy,
    periods: [period(20, 8, 'Grace', false, 2), period(21, 9, 'Open', true, 2)],
  } as unknown as PeriodCalendarDto,
};

const SeededStrings: Record<string, string> = {
  'periods.title': 'Periods',
  'periods.project': 'Project',
  'periods.pickProject': 'Pick a project',
  'periods.key': 'Period',
  'periods.reopen': 'Reopen period',
  'periods.overview.title': 'All projects · current period',
  'periods.overview.project': 'Project',
  'periods.overview.current': 'Current period',
  'periods.overview.state': 'State',
  'periods.overview.closes': 'Closes',
  'periods.overview.stats': 'Periods of all projects',
  'periods.overview.stat.open': 'open',
  'periods.overview.stat.grace': 'in grace period',
  'periods.overview.stat.soon': 'closing in ≤ 3 days',
  'periods.overview.noMatch': 'No project matches this filter',
  'periods.year.title': '{project} · {year}',
  'periods.tile.readOnly': 'read-only',
  'periods.tile.opens': 'opens {date}',
  'periods.tile.grace': 'grace period until {date} · edits are late',
  'periods.tile.closesToday': 'closes today',
  'periods.tile.closesIn.one': 'closes in {count} day',
  'periods.tile.closesIn.other': 'closes in {count} days',
  'periods.tile.now': 'now',
  'status.period.Open': 'Open',
  'status.period.Closed': 'Closed',
  'status.period.Grace': 'Grace period',
  'status.period.Scheduled': 'Not open yet',
  'status.project.Active': 'Active',
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function serve(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      if (url.includes('/api/v1/me')) {
        return json({
          denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false,
          permissions: [], simulatedForUserId: null, userId: 1, userName: 'tester',
        });
      }
      const calendar = /\/api\/v1\/projects\/(\d+)\/periods/.exec(url);
      if (calendar !== null) return json(calendars[Number(calendar[1])]);
      if (url.includes('/api/v1/projects')) return json({ items: projects, nextCursor: null, totalCount: projects.length });

      return json(null);
    }),
  );
}

let search = '';

function LocationProbe(): JSX.Element {
  search = useLocation().search;

  return <span />;
}

function show(entry = '/admin/periods'): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[entry]}>
        <QueryClientProvider client={client}>
          <PeriodsPage />
          <LocationProbe />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

beforeEach(async () => {
  search = '';
  serve();
  await loadCatalog('en', 'private');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('PeriodsPage: огляд усіх проєктів і плитки року (UI-33)', () => {
  it('без вибору — таблиця всіх проєктів із поточним періодом, а не «Pick a project»', async () => {
    show();

    const overview = await screen.findByTestId('periods-overview');
    const table = await within(overview).findByRole('table');

    await within(table).findByRole('button', { name: 'ALPHA' });
    expect(within(table).getByRole('button', { name: 'BETA' })).toBeDefined();
    // Поточний період — людською назвою, не ключем `202609`.
    expect(within(table).getAllByText('September 2026')).toHaveLength(2);
    expect(within(table).getByText(/closes in 2 days/)).toBeDefined();

    expect(screen.queryByRole('heading', { level: 4, name: 'Pick a project' })).toBeNull();
    expect(screen.queryByTestId('periods-year')).toBeNull();
    expect(new URLSearchParams(search).get('projectId')).toBeNull();
  });

  it('смуга показників фільтрує перелік: «in grace period» лишає лише BETA', async () => {
    show();

    const overview = await screen.findByTestId('periods-overview');
    await within(overview).findByRole('button', { name: 'ALPHA' });

    // UI RC9: смуга показників — над сторінкою (макет), а не всередині блоку «All projects».
    expect(within(overview).queryByRole('button', { name: /in grace period/ })).toBeNull();
    await userEvent.click(await screen.findByRole('button', { name: /in grace period/ }));

    await waitFor(() => {
      expect(within(overview).queryByRole('button', { name: 'ALPHA' })).toBeNull();
    });
    expect(within(overview).getByRole('button', { name: 'BETA' })).toBeDefined();
    expect(new URLSearchParams(search).get('stat')).toBe('grace');
  });

  it('вибір проєкту в таблиці — плитки року зі станом і датами; клац плитки виділяє рядок календаря', async () => {
    show();

    const overview = await screen.findByTestId('periods-overview');
    await userEvent.click(await within(overview).findByRole('button', { name: 'ALPHA' }));

    await waitFor(() => {
      expect(new URLSearchParams(search).get('projectId')).toBe('1');
    });

    const year = await screen.findByTestId('periods-year');
    expect(within(year).getByRole('heading', { name: 'ALPHA · 2026' })).toBeDefined();

    const tiles = within(year).getAllByRole('button');
    expect(tiles).toHaveLength(3);
    expect(tiles.map((tile) => tile.getAttribute('data-state'))).toEqual(['Closed', 'Open', 'Scheduled']);
    expect(within(tiles[0] as HTMLElement).getByText('read-only')).toBeDefined();
    expect(within(tiles[1] as HTMLElement).getByText(/closes in 20 days/)).toBeDefined();
    expect(within(tiles[2] as HTMLElement).getByText(/^opens /)).toBeDefined();

    // ⛔ «Ще не відкрито» — пунктир за макетом, БЕЗ прозорості: `opacity` топила приглушений
    // текст плитки до 3.34:1 (batch-2-a, дефект 5).
    for (const tile of tiles) expect(tile.style.opacity).toBe('');
    expect(tiles[2]?.getAttribute('style')).toMatch(/border: 1px dashed/);
    expect(tiles[1]?.getAttribute('style')).toMatch(/border: 1px solid/);

    // ⛔ Рішення 1: плитки не несуть дій над періодом («Open October»/«Close»).
    expect(within(year).queryByText(/Reopen|Close now|Open period/)).toBeNull();

    await userEvent.click(tiles[0] as HTMLElement);
    expect(tiles[0]?.getAttribute('aria-pressed')).toBe('true');
    expect(document.querySelector('[data-period-row="202608"]')?.getAttribute('data-selected')).toBe('true');
    expect(document.querySelector('[data-period-row="202609"]')?.getAttribute('data-selected')).toBeNull();
  });
});

describe('PeriodsOverview: «closes in N days» і рядок плитки', () => {
  const calendar = calendars[1] as PeriodCalendarDto;
  const siteDate = (value: string): string => value.slice(0, 10);
  const base = calendar.periods[1]!;

  it('доба до виключної межі — «today», не «in 1 day»', () => {
    const p = { ...base, endsAt: new Date(now + DayMs / 2).toISOString() };
    expect(daysLeft(p, now)).toBe(0);
    expect(periodNote(p, calendar, siteDate, now)).toMatch(/^closes today · /);
  });

  it('закритий і ще не відкритий періоди не мають «днів до закриття»', () => {
    expect(daysLeft({ ...base, state: 'Closed' }, now)).toBeNull();
    expect(daysLeft({ ...base, state: 'Scheduled' }, now)).toBeNull();
  });

  it('пільговий строк і перевідкриття пояснені словами', () => {
    expect(periodNote({ ...base, state: 'Grace' }, calendar, siteDate, now)).toMatch(/edits are late/);
  });
});
