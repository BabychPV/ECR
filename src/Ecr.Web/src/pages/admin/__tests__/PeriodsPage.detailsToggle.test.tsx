import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { testTheme } from '@/test/render';

/**
 * Periods dedup (людина 27.11): місяці — плитками (огляд), а таблиця «Month / State / Locks on» — у згорнутому «Details»
 * (за замовчуванням закрито, вибір пам'ятається на людину). Плитки видно завжди.
 */

const calendar = {
  projectId: 7,
  periodKind: 'Monthly' as const,
  currentPeriodMode: 'Auto' as const,
  timeZoneId: 'Europe/Kyiv',
  policy: { id: 1, code: 'ECR-Standard', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 45 },
  periods: [
    { id: 3, periodKey: 202603, year: 2026, sequence: 3, startsAt: '2026-03-01T00:00:00+02:00', endsAt: '2099-05-15T00:00:00+03:00', state: 'Open', graceEndsAt: '2026-04-15T00:00:00+03:00', reopenedUntil: null, isCurrent: true },
  ],
};

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/api/v1/me')) {
        return json({ denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false, permissions: [], simulatedForUserId: null, userId: 1, userName: 'tester' });
      }
      if (url.includes('/api/v1/projects') && url.includes('/periods')) return json(calendar);
      if (url.includes('/api/v1/projects')) return json({ items: [], nextCursor: null, totalCount: 0 });

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
  window.localStorage.clear();
});

const Slow = 300_000;

describe('PeriodsPage: таблиця місяців у «Details»', () => {
  it(
    'таблиця схована за замовчуванням, перемикач її відкриває і пам’ятає вибір, плитки видно завжди',
    async () => {
      mockFetch();
      const user = userEvent.setup();
      show();

      const toggle = await screen.findByRole('button', { name: /⟦periods\.details⟧/ }, { timeout: 60_000 });
      const tile = await screen.findByText((_, el) => el?.getAttribute('data-period-tile') === '202603', {}, { timeout: 60_000 });
      expect(tile).toBeTruthy();

      expect(toggle.getAttribute('aria-expanded')).toBe('false');
      const bodyId = toggle.getAttribute('aria-controls') ?? '';
      expect(document.getElementById(bodyId)?.hasAttribute('hidden')).toBe(true);
      expect(screen.queryByRole('columnheader')).toBeNull();

      await user.click(toggle);
      expect(toggle.getAttribute('aria-expanded')).toBe('true');
      expect(document.getElementById(bodyId)?.hasAttribute('hidden')).toBe(false);
      expect(screen.getAllByRole('columnheader').length).toBeGreaterThan(0);
      expect(document.querySelector('[data-period-tile="202603"]')).toBeTruthy();

      // Вибір людини пам'ятається: після повторного показу таблиця вже відкрита.
      await waitFor(() => expect(window.localStorage.getItem('ecr.note.periods-details.1')).toBe('0'));
    },
    Slow,
  );

  it(
    'клац по плитці розгортає «Details» до рядка цього місяця',
    async () => {
      mockFetch();
      const user = userEvent.setup();
      show();

      const toggle = await screen.findByRole('button', { name: /⟦periods\.details⟧/ }, { timeout: 60_000 });
      const tile = await screen.findByText((_, el) => el?.getAttribute('data-period-tile') === '202603', {}, { timeout: 60_000 });
      expect(toggle.getAttribute('aria-expanded')).toBe('false');

      await user.click(tile.closest('[data-period-tile]') as HTMLElement);
      expect(toggle.getAttribute('aria-expanded')).toBe('true');
      expect(document.querySelector('[data-period-row="202603"]')).toBeTruthy();
    },
    Slow,
  );
});
