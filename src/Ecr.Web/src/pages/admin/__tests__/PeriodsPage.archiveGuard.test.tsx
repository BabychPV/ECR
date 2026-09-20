import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { testTheme } from '@/test/render';

/**
 * Ð—Ð°Ð¿Ð¾Ð±Ñ–Ð¶Ð½Ð¸Ðº Ð°Ñ€Ñ…Ñ–Ð²Ð°Ñ†Ñ–Ñ— Ð´ÐµÐ³Ñ€Ð°Ð´ÑƒÐ²Ð°Ð² Ñƒ Ð±Ñ–Ðº Ð”ÐžÐ—Ð’ÐžÐ›Ð£.
 *
 * â›” `POST /archive` Ð²Ñ–Ð´Ð¼Ð¾Ð²Ð»ÑÑ” `409 ECR-PRD-0409`, ÐºÐ¾Ð»Ð¸ Ñ” Ñ…Ð¾Ñ‡ Ð¾Ð´Ð¸Ð½ Ð½ÐµÐ·Ð°ÐºÑ€Ð¸Ñ‚Ð¸Ð¹
 * Ð¿ÐµÑ€Ñ–Ð¾Ð´, Ñ– Ð´Ñ–Ð°Ð»Ð¾Ð³ Ð¿Ñ–Ð´Ñ‚Ð²ÐµÑ€Ð´Ð¶ÐµÐ½Ð½Ñ Ð¿Ð¾Ð¿ÐµÑ€ÐµÐ´Ð¶Ð°Ñ” Ð¿Ñ€Ð¾ Ñ†Ðµ Ð”Ðž ÐºÐ»Ñ–ÐºÑƒ â€” ÑÐ°Ð¼Ðµ Ð·Ð°Ñ€Ð°Ð´Ð¸
 * Ñ†ÑŒÐ¾Ð³Ð¾ Ð¿Ð¾Ð¿ÐµÑ€ÐµÐ´Ð¶ÐµÐ½Ð½Ñ Ð¹Ð¾Ð³Ð¾ Ð¹ Ð¿Ð¸ÑÐ°Ð»Ð¸ (Ð°ÑƒÐ´Ð¸Ñ‚-Ð¿Ð°Ñ 8, Ð¿.2).
 *
 * âš  ÐÐ»Ðµ Ð¿ÐµÑ€ÐµÐ»Ñ–Ðº Ð½ÐµÐ·Ð°ÐºÑ€Ð¸Ñ‚Ð¸Ñ… Ñ€Ð°Ñ…ÑƒÐ²Ð°Ð²ÑÑ ÑÐº `(periods.data?.periods ?? [])`. ÐŸÑ€Ð¸
 * Ð²Ñ–Ð´Ð¼Ð¾Ð²Ñ– `GET /projects/{id}/periods` Ð²Ñ–Ð½ ÑÑ‚Ð°Ð²Ð°Ð² ÐŸÐžÐ ÐžÐ–ÐÐ†Ðœ â€” Ð¿Ð¾Ð¿ÐµÑ€ÐµÐ´Ð¶ÐµÐ½Ð½Ñ
 * Ð·Ð½Ð¸ÐºÐ°Ð»Ð¾, Ð° ÐºÐ½Ð¾Ð¿ÐºÐ° Â«ÐÑ€Ñ…Ñ–Ð²ÑƒÐ²Ð°Ñ‚Ð¸Â» Ð ÐžÐ—Ð‘Ð›ÐžÐšÐžÐ’Ð£Ð’ÐÐ›ÐÐ¡Ð¬. Ð¢Ð¾Ð±Ñ‚Ð¾ Ñ€Ñ–Ð²Ð½Ð¾ Ñ‚Ð¾Ð´Ñ–, ÐºÐ¾Ð»Ð¸
 * ÐºÐ»Ñ–Ñ”Ð½Ñ‚ Ð½Ðµ Ð·Ð½Ð°Ð² ÑÑ‚Ð°Ð½Ñƒ Ð¿ÐµÑ€Ñ–Ð¾Ð´Ñ–Ð², Ð²Ñ–Ð½ Ð¿Ð¾Ð²Ñ–Ð´Ð¾Ð¼Ð»ÑÐ², Ñ‰Ð¾ Ð°Ñ€Ñ…Ñ–Ð²ÑƒÐ²Ð°Ñ‚Ð¸ Ð±ÐµÐ·Ð¿ÐµÑ‡Ð½Ð¾.
 *
 * â›” Ð”Ð°Ð½Ñ– Ð²Ñ–Ð´ Ñ†ÑŒÐ¾Ð³Ð¾ Ð½Ðµ ÑÑ‚Ñ€Ð°Ð¶Ð´Ð°Ð»Ð¸ â€” ÑÐµÑ€Ð²ÐµÑ€ Ð¾Ð´Ð½Ð°ÐºÐ¾Ð²Ð¾ Ð²Ñ–Ð´Ð¼Ð¾Ð²Ð»ÑÑ”. Ð”Ð¾Ñ€Ð¾Ð³Ð¾ Ñ–Ð½ÑˆÐµ:
 * Ð¿Ð¾Ð¿ÐµÑ€ÐµÐ´Ð¶ÐµÐ½Ð½Ñ, ÑÐºÐµ Ð·Ð½Ð¸ÐºÐ°Ñ” ÑÐ°Ð¼Ðµ Ð² Ð½ÐµÐ²Ð¸Ð·Ð½Ð°Ñ‡ÐµÐ½Ð¾ÑÑ‚Ñ–, Ð²Ñ‡Ð¸Ñ‚ÑŒ Ð¹Ð¾Ð¼Ñƒ Ð½Ðµ Ð²Ñ–Ñ€Ð¸Ñ‚Ð¸.
 * Ð—Ð°Ð¿Ð¾Ð±Ñ–Ð¶Ð½Ð¸Ðº Ð¼Ð°Ñ” Ð´ÐµÐ³Ñ€Ð°Ð´ÑƒÐ²Ð°Ñ‚Ð¸ Ð² Ð±Ñ–Ðº Ð—ÐÐ‘ÐžÐ ÐžÐÐ˜.
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

const Refusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'ÐºÐ°Ð»ÐµÐ½Ð´Ð°Ñ€ Ð¿ÐµÑ€Ñ–Ð¾Ð´Ñ–Ð² Ð¿Ñ€Ð¾Ñ‡Ð¸Ñ‚Ð°Ñ‚Ð¸ Ð½Ðµ Ð²Ð´Ð°Ð»Ð¾ÑÑ',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-periods-1',
  messageKey: 'err.ECR-SYS-0500.unexpected',
};

function period(state: string, n: number): Record<string, unknown> {
  return {
    id: n,
    periodKey: 202600 + n,
    sequence: n,
    startsAt: '2026-01-01',
    endsAt: '2026-02-01',
    state,
    graceEndsAt: null,
    reopenedUntil: null,
    isCurrent: false,
  };
}

/** ÐšÐ°Ð»ÐµÐ½Ð´Ð°Ñ€, Ñƒ ÑÐºÐ¾Ð¼Ñƒ Ð’Ð¡Ð† Ð¿ÐµÑ€Ñ–Ð¾Ð´Ð¸ Ð·Ð°ÐºÑ€Ð¸Ñ‚Ñ– â€” Ñ‚Ð¾Ð±Ñ‚Ð¾ Ð°Ñ€Ñ…Ñ–Ð²ÑƒÐ²Ð°Ñ‚Ð¸ ÑÐ¿Ñ€Ð°Ð²Ð´Ñ– Ð¼Ð¾Ð¶Ð½Ð°. */
const closedCalendar = {
  projectId: 7,
  periodKind: 'Monthly' as const,
  currentPeriodMode: 'Auto' as const,
  timeZoneId: 'Europe/Kyiv',
  policy: {
    id: 1,
    code: 'ECR-Standard',
    openOffsetDays: 0,
    graceOffsetDays: 15,
    hardCloseOffsetDays: 45,
    yearGraceOffsetDays: 45,
  },
  periods: [period('Closed', 1), period('Closed', 2)],
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json',
    },
  });
}

function mockFetch(calendarFails: boolean): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Project.Manage'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (url.includes('/api/v1/projects') && url.includes('/periods')) {
        return calendarFails ? json(Refusal, 500) : json(closedCalendar);
      }

      if (url.includes('/api/v1/projects')) {
        return json({ items: [project], nextCursor: null, totalCount: 1 });
      }

      return json(null);
    }),
  );
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

/** âš  Ð¢Ð° ÑÐ°Ð¼Ð° Ð¼ÐµÐ¶Ð°, Ñ‰Ð¾ Ð² Ñ€ÐµÑˆÑ‚Ñ– Ñ‚ÐµÑÑ‚Ñ–Ð² Ñ†Ñ–Ñ”Ñ— ÑÑ‚Ð¾Ñ€Ñ–Ð½ÐºÐ¸: Ð²Ð¾Ð½Ð° Ð²Ð°Ð¶ÐºÐ° Ð² jsdom. */
const SlowEnvTimeout = 400_000;

/**
 * Ð’Ñ–Ð´ÐºÑ€Ð¸Ð²Ð°Ñ” Ð´Ñ–Ð°Ð»Ð¾Ð³ Ð°Ñ€Ñ…Ñ–Ð²Ð°Ñ†Ñ–Ñ— Ð¹ Ð¿Ð¾Ð²ÐµÑ€Ñ‚Ð°Ñ” ÑÐ°Ð¼ Ð´Ñ–Ð°Ð»Ð¾Ð³ Ñ–Ð· Ð¹Ð¾Ð³Ð¾ ÐºÐ½Ð¾Ð¿ÐºÐ¾ÑŽ
 * Ð¿Ñ–Ð´Ñ‚Ð²ÐµÑ€Ð´Ð¶ÐµÐ½Ð½Ñ.
 *
 * âš  Ð£ÑÐµ ÑˆÑƒÐºÐ°Ñ”Ñ‚ÑŒÑÑ Ð¡ÐÐœÐ• Ð’ Ð”Ð†ÐÐ›ÐžÐ—Ð†, Ñ– Ñ†Ðµ Ð½Ðµ Ð¿ÐµÐ´Ð°Ð½Ñ‚Ð¸Ð·Ð¼. ÐšÐ½Ð¾Ð¿ÐºÐ° Ð²Ñ–Ð´ÐºÑ€Ð¸Ñ‚Ñ‚Ñ Ð¹
 * ÐºÐ½Ð¾Ð¿ÐºÐ° Ð¿Ñ–Ð´Ñ‚Ð²ÐµÑ€Ð´Ð¶ÐµÐ½Ð½Ñ Ð¼Ð°ÑŽÑ‚ÑŒ Ð¾Ð´Ð½Ð°ÐºÐ¾Ð²Ð¸Ð¹ Ð¿Ñ–Ð´Ð¿Ð¸Ñ; Ð° Ð¿Ñ€Ð¸ Ð²Ñ–Ð´Ð¼Ð¾Ð²Ñ– ÐºÐ°Ð»ÐµÐ½Ð´Ð°Ñ€Ñ
 * ÑÑ‚Ð¾Ñ€Ñ–Ð½ÐºÐ° Ð¿Ñ–Ð´ Ð¼Ð¾Ð´Ð°Ð»ÐºÐ¾ÑŽ Ð¿Ð¾ÐºÐ°Ð·ÑƒÑ” Ð²Ð»Ð°ÑÐ½Ð¸Ð¹ Ð±Ð°Ð½ÐµÑ€ Ñ‡ÐµÑ€ÐµÐ· `AsyncBoundary` â€” Ñ‚Ð¾Ð±Ñ‚Ð¾
 * `role="alert"` Ð½Ð° ÑÑ‚Ð¾Ñ€Ñ–Ð½Ñ†Ñ– Ð²Ð¶Ðµ Ð´Ð²Ð°, Ñ– `screen.getByRole('alert')` Ð¿Ð°Ð´Ð°Ñ” Ð·
 * Â«Found multiple elementsÂ». ÐšÐ¾Ñ€Ð¸ÑÑ‚ÑƒÐ²Ð°Ñ‡ Ð¿Ñ€Ð¸ Ð²Ñ–Ð´ÐºÑ€Ð¸Ñ‚Ñ–Ð¹ Ð¼Ð¾Ð´Ð°Ð»Ñ†Ñ– Ð±Ð°Ñ‡Ð¸Ñ‚ÑŒ Ð»Ð¸ÑˆÐµ
 * Ñ—Ñ— Ð²Ð¼Ñ–ÑÑ‚, Ñ‚Ð¾Ð¶ Ñ– Ñ‚ÐµÑÑ‚ Ð¼Ð°Ñ” Ð´Ð¸Ð²Ð¸Ñ‚Ð¸ÑÑ Ñ‚ÑƒÐ´Ð¸ Ð¶.
 */
async function openArchiveDialog(): Promise<{
  dialog: HTMLElement;
  confirm: HTMLElement;
}> {
  const user = userEvent.setup();
  const open = await screen.findByRole('button', { name: /periods\.archive/ }, {
    timeout: SlowEnvTimeout,
  });

  await user.click(open);

  const dialog = await screen.findByRole('dialog', {}, { timeout: SlowEnvTimeout });
  const confirm = [...dialog.querySelectorAll('button')].find((button) =>
    (button.textContent ?? '').includes('periods.archive'),
  );

  if (confirm === undefined) throw new Error('ÐºÐ½Ð¾Ð¿ÐºÐ¸ Ð¿Ñ–Ð´Ñ‚Ð²ÐµÑ€Ð´Ð¶ÐµÐ½Ð½Ñ Ð² Ð´Ñ–Ð°Ð»Ð¾Ð·Ñ– Ð½ÐµÐ¼Ð°Ñ”');

  return { dialog, confirm };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('PeriodsPage: Ð·Ð°Ð¿Ð¾Ð±Ñ–Ð¶Ð½Ð¸Ðº Ð°Ñ€Ñ…Ñ–Ð²Ð°Ñ†Ñ–Ñ— Ð´ÐµÐ³Ñ€Ð°Ð´ÑƒÑ” Ð² Ð±Ñ–Ðº Ð—ÐÐ‘ÐžÐ ÐžÐÐ˜', () => {
  it(
    'ÐºÐ°Ð»ÐµÐ½Ð´Ð°Ñ€ Ð½Ðµ Ð¿Ñ€Ð¸Ñ—Ñ…Ð°Ð² â€” ÐºÐ½Ð¾Ð¿ÐºÐ° Ð»Ð¸ÑˆÐ°Ñ”Ñ‚ÑŒÑÑ Ð·Ð°Ð±Ð»Ð¾ÐºÐ¾Ð²Ð°Ð½Ð¾ÑŽ, Ñ– Ð¿Ñ€Ð¸Ñ‡Ð¸Ð½Ñƒ Ð²Ð¸Ð´Ð½Ð¾',
    async () => {
      mockFetch(true);
      show();

      const { dialog, confirm } = await openArchiveDialog();

      /*
       * â›” Ð“Ð¾Ð»Ð¾Ð²Ð½Ðµ Ñ‚Ð²ÐµÑ€Ð´Ð¶ÐµÐ½Ð½Ñ, Ñ– ÑÐ°Ð¼Ðµ Ð²Ð¾Ð½Ð¾ Ð±ÑƒÐ»Ð¾ Ñ…Ð¸Ð±Ð½Ð¸Ð¼: Ð´Ð¾ Ð¿Ñ€Ð°Ð²ÐºÐ¸ Ð²Ñ–Ð´Ð¼Ð¾Ð²Ð°
       * Ð·Ð°Ð¿Ð¸Ñ‚Ñƒ Ñ€Ð¾Ð±Ð¸Ð»Ð° `openPeriods` Ð¿Ð¾Ñ€Ð¾Ð¶Ð½Ñ–Ð¼, Ð¿Ð¾Ð¿ÐµÑ€ÐµÐ´Ð¶ÐµÐ½Ð½Ñ Ð·Ð½Ð¸ÐºÐ°Ð»Ð¾, Ð° Ñ†Ñ
       * ÐºÐ½Ð¾Ð¿ÐºÐ° ÑÑ‚Ð°Ð²Ð°Ð»Ð° Ð”ÐžÐ¡Ð¢Ð£ÐŸÐÐžÐ®.
       */
      expect(confirm).toHaveProperty('disabled', true);

      const alert = await waitFor(() => within(dialog).getByRole('alert'));

      expect(alert.textContent ?? '').toContain('ÐºÐ°Ð»ÐµÐ½Ð´Ð°Ñ€ Ð¿ÐµÑ€Ñ–Ð¾Ð´Ñ–Ð² Ð¿Ñ€Ð¾Ñ‡Ð¸Ñ‚Ð°Ñ‚Ð¸ Ð½Ðµ Ð²Ð´Ð°Ð»Ð¾ÑÑ');
      expect(alert.textContent ?? '').toContain('ECR-SYS-0500');
    },
    SlowEnvTimeout,
  );

  it(
    'ÑƒÑÑ– Ð¿ÐµÑ€Ñ–Ð¾Ð´Ð¸ Ð·Ð°ÐºÑ€Ð¸Ñ‚Ñ– â€” ÐºÐ½Ð¾Ð¿ÐºÐ° Ð”ÐžÐ¡Ð¢Ð£ÐŸÐÐ: Ð·Ð°Ð±Ð¾Ñ€Ð¾Ð½Ð° Ð½Ðµ ÑÑ‚Ð°Ð»Ð° Ñ‚Ð¾Ñ‚Ð°Ð»ÑŒÐ½Ð¾ÑŽ',
    async () => {
      /*
       * âš  Ð”Ð·ÐµÑ€ÐºÐ°Ð»Ð¾, Ð±ÐµÐ· ÑÐºÐ¾Ð³Ð¾ Ð¿ÐµÑ€ÑˆÐµ Ñ‚Ð²ÐµÑ€Ð´Ð¶ÐµÐ½Ð½Ñ Ð½Ñ–Ñ‡Ð¾Ð³Ð¾ Ð½Ðµ Ð²Ð°Ñ€Ñ‚Ðµ. ÐÐ°Ð¹Ð¿Ñ€Ð¾ÑÑ‚Ñ–ÑˆÐ¸Ð¹
       * ÑÐ¿Ð¾ÑÑ–Ð± Â«Ð¿Ð¾Ð»Ð°Ð³Ð¾Ð´Ð¸Ñ‚Ð¸Â» Ð·Ð°Ð¿Ð¾Ð±Ñ–Ð¶Ð½Ð¸Ðº â€” Ð·Ð°Ð±Ð»Ð¾ÐºÑƒÐ²Ð°Ñ‚Ð¸ ÐºÐ½Ð¾Ð¿ÐºÑƒ Ð½Ð°Ð·Ð°Ð²Ð¶Ð´Ð¸; Ñ‚Ð¾Ð´Ñ–
       * Ð°Ñ€Ñ…Ñ–Ð²ÑƒÐ²Ð°Ñ‚Ð¸ Ð½Ðµ Ð¼Ð¾Ð¶Ð½Ð° Ð±ÑƒÐ»Ð¾ Ð± Ð½Ñ–ÐºÐ¾Ð»Ð¸, Ñ– Ð¿ÐµÑ€ÑˆÐ¸Ð¹ Ð²Ð¸Ð¿Ð°Ð´Ð¾Ðº Ð»Ð¸ÑˆÐ°Ð²ÑÑ Ð± Ð·ÐµÐ»ÐµÐ½Ð¸Ð¼.
       */
      mockFetch(false);
      show();

      const { dialog, confirm } = await openArchiveDialog();

      await waitFor(() => {
        expect(confirm).toHaveProperty('disabled', false);
      });

      expect(within(dialog).queryByRole('alert')).toBeNull();
    },
    SlowEnvTimeout,
  );
});
