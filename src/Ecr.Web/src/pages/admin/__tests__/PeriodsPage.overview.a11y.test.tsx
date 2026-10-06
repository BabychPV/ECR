import { describe as suite, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClientProvider } from '@tanstack/react-query';
import { describe as report, findViolations } from '@/test/a11y';
import { Themes, createScanClient, settleQueries } from '@/test/__tests__/a11yFixtures';
import { mantineProviderProps } from '@/shared/theme/provider';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';

/**
 * UI-33: плитки року й «All projects» під axe в обох темах.
 *
 * ⚠ Окремо від `accessibility.part3`: там фікстура `/projects` порожня, тож
 * огляд і плитки взагалі не малюються, і axe їх не бачив би.
 */

const DayMs = 24 * 60 * 60 * 1000;
const iso = (days: number): string => new Date(Date.now() + days * DayMs).toISOString();

const projects = [
  { id: 1, code: 'ALPHA', status: 'Active', periodKind: 'Monthly', timeZoneId: 'UTC', currentPeriodId: 11, periodCount: 4 },
  { id: 2, code: 'BETA', status: 'Draft', periodKind: 'Monthly', timeZoneId: 'UTC', currentPeriodId: null, periodCount: 0 },
];

const policy = { id: 1, code: 'STD', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 0 };

function period(id: number, sequence: number, state: string, isCurrent: boolean, endsInDays: number) {
  return {
    id, periodKey: 202600 + sequence, year: 2026, sequence, state, isCurrent,
    startsAt: `2026-${String(sequence).padStart(2, '0')}-01T00:00:00Z`,
    endsAt: iso(endsInDays), graceEndsAt: null, reopenedUntil: null,
  };
}

const calendar = {
  projectId: 1, timeZoneId: 'UTC', periodKind: 'Monthly', currentPeriodMode: 'Auto', policy,
  periods: [period(10, 7, 'Closed', false, -40), period(11, 8, 'Grace', false, 2), period(12, 9, 'Open', true, 20), period(13, 10, 'Scheduled', false, 60)],
};

beforeEach(() => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      let body: unknown = null;
      if (url.includes('/ui-strings/')) body = { languageCode: 'en', revision: 1, strings: {} };
      else if (/\/api\/v1\/projects\/1\/periods/.test(url)) body = calendar;
      else if (/\/api\/v1\/projects\/\d+\/periods/.test(url)) body = { ...calendar, projectId: 2, periods: [] };
      else if (url.includes('/api/v1/projects')) body = { items: projects, nextCursor: null, totalCount: 2 };

      return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

suite.each(Themes)('UI-33 Periods: огляд і плитки (%s)', (colorScheme) => {
  it('ФВ-14.16: без порушень critical і serious', async () => {
    const client = createScanClient();
    const { container } = render(
      <MantineProvider {...mantineProviderProps} forceColorScheme={colorScheme}>
        <QueryClientProvider client={client}>
          <MemoryRouter initialEntries={['/admin/periods?projectId=1']}>
            <PeriodsPage />
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>,
    );

    await settleQueries(client);
    await screen.findByTestId('periods-year');
    await screen.findByTestId('periods-overview');

    const violations = await findViolations(container);

    expect(violations, report(violations)).toHaveLength(0);
  });
});
