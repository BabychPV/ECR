import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { testTheme } from '@/test/render';

/**
 * Reopen на плитці місяця (людина 27.11): таблиця згорнута в «Details», тож дія «Reopen» з'явилась і на плитці — лише на
 * закритому місяці, лише з правом (`Period.Reopen` + грант Manage), через ТОЙ САМИЙ діалог, без кліку по самій плитці.
 * ⚠ Каталог не завантажується: `t()` повертає `⟦ключ (param=val)⟧`.
 */

const project = { id: 7, code: 'PRJ-7', status: 'Active' as const, periodKind: 'Monthly' as const, periodCount: 2, currentPeriodId: null, timeZoneId: 'Europe/Kyiv' };

const calendar = {
  projectId: 7,
  periodKind: 'Monthly' as const,
  currentPeriodMode: 'Auto' as const,
  timeZoneId: 'Europe/Kyiv',
  policy: { id: 1, code: 'ECR-Standard', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 45 },
  periods: [
    { id: 1, periodKey: 202601, year: 2026, sequence: 1, startsAt: '2026-01-01T00:00:00+02:00', endsAt: '2026-03-17T00:00:00+02:00', state: 'Closed', graceEndsAt: '2026-02-15T00:00:00+02:00', reopenedUntil: null, isCurrent: false },
    { id: 2, periodKey: 202602, year: 2026, sequence: 2, startsAt: '2026-02-01T00:00:00+02:00', endsAt: '2099-04-15T00:00:00+03:00', state: 'Open', graceEndsAt: '2099-03-15T00:00:00+02:00', reopenedUntil: null, isCurrent: true },
  ],
};

function mockFetch(permissions: string[], grants: Record<string, string>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/api/v1/me')) {
        return json({ denies: [], grants, isSimulation: false, language: 'en', mustChangePassword: false, permissions, simulatedForUserId: null, userId: 1, userName: 'tester' });
      }
      if (url.includes('/document-reopens')) return json({ canClose: false, items: [] });
      if (url.includes('/api/v1/projects') && url.includes('/periods')) return json(calendar);
      if (url.includes('/api/v1/projects')) return json({ items: [project], nextCursor: null, totalCount: 1 });

      return json(null);
    }),
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/projects/7/periods']}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <PeriodsPage projectId={7} />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

beforeEach(() => {
  window.localStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

const Slow = 300_000;
const tile = (key: number): Promise<HTMLElement> =>
  screen.findByText((_, el) => el?.getAttribute('data-period-tile') === String(key), {}, { timeout: 60_000 }).then((el) => el.closest('[data-period-tile]') as HTMLElement);

describe('PeriodsPage: Reopen on month tiles', () => {
  it(
    'a Locked month tile has «Reopen <Month YYYY>»; the Open month does not',
    async () => {
      mockFetch(['Project.Manage', 'Period.Reopen'], { 'Project:7': 'Manage' });
      show();
      await tile(202601);

      const buttons = await screen.findAllByRole('button', { name: '⟦periods.tile.reopenAria (period=January 2026)⟧' });
      expect(buttons).toHaveLength(1);
      expect(buttons[0]?.textContent).toBe('⟦periods.tile.reopen⟧');
      expect(document.querySelectorAll('[data-tile-reopen]')).toHaveLength(1);
      expect(document.querySelector('[data-tile-reopen="202602"]')).toBeNull();
      // Дія — сестра плитки, а не нащадок: `<button>` у `<button>` недопустимий.
      expect(buttons[0]?.closest('[data-period-tile]')).toBeNull();
    },
    Slow,
  );

  it(
    'no Period.Reopen permission, or no Manage grant: no action on tiles',
    async () => {
      mockFetch(['Project.Manage'], { 'Project:7': 'Manage' });
      show();
      await tile(202601);
      expect(document.querySelector('[data-tile-reopen]')).toBeNull();
    },
    Slow,
  );

  it(
    'no Manage grant on the project: no action on tiles',
    async () => {
      mockFetch(['Period.Reopen'], {});
      show();
      await tile(202601);
      expect(document.querySelector('[data-tile-reopen]')).toBeNull();
    },
    Slow,
  );

  it(
    'click opens the reopen dialog for that month and does not trigger the tile click (Details stay collapsed)',
    async () => {
      mockFetch(['Project.Manage', 'Period.Reopen'], { 'Project:7': 'Manage' });
      const user = userEvent.setup();
      show();
      const jan = await tile(202601);
      await user.click(await screen.findByRole('button', { name: '⟦periods.tile.reopenAria (period=January 2026)⟧' }));

      const dialog = await screen.findByRole('dialog');
      expect(dialog.textContent).toContain('⟦periods.reopenTitle (period=');
      // Плитку не «натиснуто» (onPick розгорнув би Details і виділив рядок).
      expect(jan.getAttribute('aria-pressed')).toBe('false');
      expect(document.querySelector('[data-note="periods-details"]')?.getAttribute('data-collapsed')).toBe('true');
    },
    Slow,
  );
});
