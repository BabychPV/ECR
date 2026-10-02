import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { testTheme } from '@/test/render';

/**
 * Запобіжник архівації деградував у бік ДОЗВОЛУ.
 *
 * ⛔ `POST /archive` відмовляє `409 ECR-PRD-0409`, коли є хоч один незакритий
 * період, і діалог підтвердження попереджає про це ДО кліку — саме заради
 * цього попередження його й писали (аудит-пас 8, п.2).
 *
 * ⚠ Але перелік незакритих рахувався як `(periods.data?.periods ?? [])`. При
 * відмові `GET /projects/{id}/periods` він ставав ПОРОЖНІМ — попередження
 * зникало, а кнопка «Архівувати» РОЗБЛОКОВУВАЛАСЬ. Тобто рівно тоді, коли
 * клієнт не знав стану періодів, він повідомляв, що архівувати безпечно.
 *
 * ⛔ Дані від цього не страждали — сервер однаково відмовляє. Дорого інше:
 * попередження, яке зникає саме в невизначеності, вчить йому не вірити.
 * Запобіжник має деградувати в бік ЗАБОРОНИ.
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
  detail: 'календар періодів прочитати не вдалося',
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

/** Календар, у якому ВСІ періоди закриті — тобто архівувати справді можна. */
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
          // F-19: дії над конкретним проєктом вимагають гранта Manage на нього.
          grants: { 'Project:7': 'Manage' },
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

/** ⚠ Та сама межа, що в решті тестів цієї сторінки: вона важка в jsdom. */
const SlowEnvTimeout = 400_000;

/**
 * Відкриває діалог архівації й повертає сам діалог із його кнопкою
 * підтвердження.
 *
 * ⚠ Усе шукається САМЕ В ДІАЛОЗІ, і це не педантизм. Кнопка відкриття й
 * кнопка підтвердження мають однаковий підпис; а при відмові календаря
 * сторінка під модалкою показує власний банер через `AsyncBoundary` — тобто
 * `role="alert"` на сторінці вже два, і `screen.getByRole('alert')` падає з
 * «Found multiple elements». Користувач при відкритій модалці бачить лише
 * її вміст, тож і тест має дивитися туди ж.
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

  if (confirm === undefined) throw new Error('кнопки підтвердження в діалозі немає');

  return { dialog, confirm };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('PeriodsPage: запобіжник архівації деградує в бік ЗАБОРОНИ', () => {
  it(
    'календар не приїхав — кнопка лишається заблокованою, і причину видно',
    async () => {
      mockFetch(true);
      show();

      const { dialog, confirm } = await openArchiveDialog();

      /*
       * ⛔ Головне твердження, і саме воно було хибним: до правки відмова
       * запиту робила `openPeriods` порожнім, попередження зникало, а ця
       * кнопка ставала ДОСТУПНОЮ.
       */
      expect(confirm).toHaveProperty('disabled', true);

      const alert = await waitFor(() => within(dialog).getByRole('alert'));

      expect(alert.textContent ?? '').toContain('календар періодів прочитати не вдалося');
      expect(alert.textContent ?? '').toContain('ECR-SYS-0500');
    },
    SlowEnvTimeout,
  );

  it(
    'усі періоди закриті — кнопка ДОСТУПНА: заборона не стала тотальною',
    async () => {
      /*
       * ⚠ Дзеркало, без якого перше твердження нічого не варте. Найпростіший
       * спосіб «полагодити» запобіжник — заблокувати кнопку назавжди; тоді
       * архівувати не можна було б ніколи, і перший випадок лишався б зеленим.
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
