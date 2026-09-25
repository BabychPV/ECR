import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { testTheme } from '@/test/render';

/**
 * Аудит-пас 8, lane2, п.2: клік «Archive» на проєкті з незакритим періодом
 * повертав `409 ECR-PRD-0409` лише ПІСЛЯ підтвердження — сам діалог
 * підтвердження нічого про цю передумову не казав.
 *
 * ⛔ Мутаційний доказ: тест перевіряє, що кнопка підтвердження в діалозі
 * ЗАБЛОКОВАНА, коли календар містить хоч один період не в стані `Closed` —
 * а не просто «попередження десь є» (косметика). Повернення
 * `disabled={openPeriods.length > 0}` на просте `disabled={false}` або
 * видалення цього пропу зробить перший тест червоним: кнопка буде
 * клікабельна попри незакритий період.
 */
const project = {
  id: 7,
  code: 'PRJ-7',
  status: 'Active' as const,
  periodKind: 'Monthly' as const,
  periodCount: 2,
  currentPeriodId: null,
  timeZoneId: 'Europe/Kyiv',
};

function period(id: number, sequence: number, state: 'Open' | 'Closed') {
  return {
    id,
    periodKey: 202600 + sequence,
    sequence,
    year: 2026,
    startsAt: '2026-01-01T00:00:00Z',
    endsAt: '2026-02-01T00:00:00Z',
    graceEndsAt: null,
    reopenedUntil: null,
    isCurrent: false,
    state,
  };
}

function calendarWith(states: Array<'Open' | 'Closed'>) {
  return {
    projectId: 7,
    periodKind: 'Monthly' as const,
    currentPeriodMode: 'Auto' as const,
    timeZoneId: 'Europe/Kyiv',
    // ⛔ Q-337, lane 2: `PeriodCalendarDto.Policy` — тултипи заголовків
    // «Range»/«Grace until» читають ці числа безумовно (контракт гарантує
    // поле); без нього рендер падає `TypeError` на `calendar.policy.X`.
    policy: {
      id: 1,
      code: 'ECR-Standard',
      openOffsetDays: 0,
      graceOffsetDays: 15,
      hardCloseOffsetDays: 45,
      yearGraceOffsetDays: 45,
    },
    periods: states.map((state, index) => period(index + 1, index + 1, state)),
  };
}

function respond(calendar: ReturnType<typeof calendarWith>): { archiveCalls: number } {
  const state = { archiveCalls: 0 };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return new Response(
          JSON.stringify({
            denies: [],
            // F-19: дії над конкретним проєктом вимагають гранта Manage на нього.
          grants: { 'Project:7': 'Manage' },
            isSimulation: false,
            language: 'en',
            mustChangePassword: false,
            permissions: ['Project.Manage'],
            simulatedForUserId: null,
            userId: 1,
            userName: 'Тестовий адміністратор',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/api/v1/projects') && url.includes('/periods')) {
        return new Response(JSON.stringify(calendar), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/api/v1/projects') && url.endsWith('/archive')) {
        state.archiveCalls += 1;

        return new Response(JSON.stringify(null), { status: 200 });
      }

      if (url.includes('/api/v1/projects')) {
        return new Response(JSON.stringify({ items: [project], nextCursor: null, totalCount: 1 }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(null), { status: 200 });
    }),
  );

  return state;
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/periods?projectId=7']}>
        <QueryClientProvider client={client}>
          <PeriodsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('PeriodsPage: попередження про незакриті періоди перед архівацією', () => {
  it(
    'відкритий період — попередження видно, кнопка підтвердження заблокована',
    async () => {
      const state = respond(calendarWith(['Closed', 'Open']));
      const user = userEvent.setup();
      show();

      const archiveButton = await screen.findByRole(
        'button',
        { name: /archive/i },
        { timeout: SlowEnvTimeout },
      );
      await user.click(archiveButton);

      const dialog = await screen.findByRole('dialog', {}, { timeout: SlowEnvTimeout });
      expect(within(dialog).getByRole('alert')).toBeDefined();

      const confirmButtons = within(dialog).getAllByRole('button', { name: /archive/i });
      const confirmButton = confirmButtons[confirmButtons.length - 1]!;
      expect(confirmButton.hasAttribute('disabled')).toBe(true);

      await user.click(confirmButton);
      expect(state.archiveCalls).toBe(0);
    },
    SlowEnvTimeout,
  );

  it(
    'усі періоди закриті — без попередження, кнопка активна',
    async () => {
      respond(calendarWith(['Closed', 'Closed']));
      const user = userEvent.setup();
      show();

      const archiveButton = await screen.findByRole(
        'button',
        { name: /archive/i },
        { timeout: SlowEnvTimeout },
      );
      await user.click(archiveButton);

      const dialog = await screen.findByRole('dialog', {}, { timeout: SlowEnvTimeout });
      expect(within(dialog).queryByRole('alert')).toBeNull();

      const confirmButtons = within(dialog).getAllByRole('button', { name: /archive/i });
      expect(confirmButtons[confirmButtons.length - 1]!.hasAttribute('disabled')).toBe(false);
    },
    SlowEnvTimeout,
  );
});
