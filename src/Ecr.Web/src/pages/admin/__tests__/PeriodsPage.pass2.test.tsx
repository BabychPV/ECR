import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { periodNote } from '@/features/projects/PeriodsOverview';
import type { PeriodCalendarDto } from '@/api/types';
import { testTheme } from '@/test/render';

/**
 * Pass2 п. 6 (людський прохід 07.11, P3) — Periods для проєкту-ЧЕРНЕТКИ:
 *  - кожна плитка місяця писала «Not open yet · opens Jan 1, 2026» — дати в минулому, які не настануть (чернетку
 *    `PeriodStateJob` не веде); червоний банер пояснював, а плитки йому суперечили. Тепер плитка неактивного проєкту
 *    каже «not open — the project is a draft» і дат відкриття не обіцяє;
 *  - смуга «2 open · 1 in grace» рахувала ВСІ проєкти над календарем одного; ✎ Projects (08.11) — смуги на вкладці
 *    проєкту немає зовсім (огляд усіх — перелік `/projects`).
 */

const policy = { id: 1, code: 'STD', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 0 };

function period(id: number, sequence: number, state: string, isCurrent: boolean) {
  return {
    id,
    periodKey: 202600 + sequence,
    year: 2026,
    sequence,
    state,
    isCurrent,
    startsAt: `2026-${String(sequence).padStart(2, '0')}-01T00:00:00Z`,
    endsAt: `2026-${String(sequence + 1).padStart(2, '0')}-15T00:00:00Z`,
    graceEndsAt: null,
    reopenedUntil: null,
  };
}

const projects = [
  { id: 1, code: 'ALPHA', status: 'Active', periodKind: 'Monthly', timeZoneId: 'UTC', currentPeriodId: 11, periodCount: 2 },
  { id: 2, code: 'DRAFTY', status: 'Draft', periodKind: 'Monthly', timeZoneId: 'UTC', currentPeriodId: null, periodCount: 3 },
];

const calendars: Record<number, PeriodCalendarDto> = {
  1: {
    projectId: 1, timeZoneId: 'UTC', periodKind: 'Monthly', currentPeriodMode: 'Auto', policy,
    periods: [period(11, 9, 'Open', true), period(12, 10, 'Grace', false)],
  } as unknown as PeriodCalendarDto,
  2: {
    projectId: 2, timeZoneId: 'UTC', periodKind: 'Monthly', currentPeriodMode: 'Auto', policy,
    periods: [period(21, 1, 'Scheduled', false), period(22, 2, 'Scheduled', false), period(23, 3, 'Scheduled', false)],
  } as unknown as PeriodCalendarDto,
};

const SeededStrings: Record<string, string> = {
  'periods.title': 'Periods',
  'periods.project': 'Project',
  'periods.pickProject': 'Pick a project',
  'periods.draftHint': 'The project is a draft: periods stay closed until it is activated.',
  'periods.overview.title': 'All projects · current period',
  'periods.overview.stats': 'Periods of all projects',
  'periods.overview.statsScope': 'All projects:',
  'periods.overview.stat.open': 'open',
  'periods.overview.stat.grace': 'in grace period',
  'periods.overview.stat.soon': 'closing in ≤ 3 days',
  'periods.year.title': '{project} · {year}',
  'periods.tile.opens': 'opens {date}',
  'periods.tile.notOpenDraft': 'not open — the project is a draft',
  'periods.tile.notOpenInactive': 'not open — the project is not active',
  'status.period.Open': 'Open',
  'status.period.Grace': 'Late (flagged)',
  'status.period.Scheduled': 'Not open yet',
  'status.project.Active': 'Active',
  'status.project.Draft': 'Draft',
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

function show(entry: string): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[entry]}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <PeriodsPage projectId={2} />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

beforeEach(async () => {
  serve();
  await loadCatalog('en', 'private');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Pass2 п. 6: Periods проєкту-чернетки', () => {
  it('Pass2: плитки чернетки не обіцяють минулих дат відкриття — «not open — the project is a draft»', async () => {
    show('/admin/periods?projectId=2');

    const year = await screen.findByTestId('periods-year');
    const tiles = await within(year).findAllByRole('button');
    expect(tiles).toHaveLength(3);

    for (const tile of tiles) {
      expect(tile.getAttribute('data-state')).toBe('Scheduled');
      expect(tile.textContent).toContain('not open — the project is a draft');
      expect(tile.textContent).not.toMatch(/opens /);
      // Читалка чує те саме, що бачить око.
      expect(tile.getAttribute('aria-label')).not.toMatch(/opens /);
    }
  });

  it('Pass2: активний проєкт і далі показує «opens {date}» для ще не відкритого місяця', () => {
    const calendar = calendars[2] as PeriodCalendarDto;
    const scheduled = calendar.periods[0]!;
    const siteDate = (value: string): string => value.slice(0, 10);

    expect(periodNote(scheduled, calendar, siteDate, Date.now(), 'Active')).toBe('opens 2026-01-01');
    expect(periodNote(scheduled, calendar, siteDate, Date.now())).toBe('opens 2026-01-01');
    expect(periodNote(scheduled, calendar, siteDate, Date.now(), 'Draft')).toBe('not open — the project is a draft');
    expect(periodNote(scheduled, calendar, siteDate, Date.now(), 'Archived')).toBe('not open — the project is not active');
  });

  // ✎ Projects (макет 08.11): смуги «All projects:» над календарем більше немає — вкладка показує ОДИН проєкт, а
  // огляд усіх — перелік `/projects`; тож і змішування обсягів, яке стерегла ця перевірка, більше не виникає.
  it('Projects 08.11: над календарем проєкту немає смуги показників усіх проєктів', async () => {
    show('/projects/2/periods');

    await screen.findByTestId('periods-year');
    expect(document.querySelector('[data-periods-stats-scope]')).toBeNull();
  });
});
