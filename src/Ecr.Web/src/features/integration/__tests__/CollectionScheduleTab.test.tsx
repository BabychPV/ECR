import { describe, it, expect, afterEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { withTestDefaults } from '@/test/render';
import { CollectionScheduleTab } from '@/features/integration/CollectionScheduleTab';
import type { CollectionSchedule } from '@/features/integration/scheduleApi';

/**
 * Розклад збору сутності джерела (ФВ-14.3).
 *
 * ⛔ Перевіряється ТІЛО й заголовки запиту, а не лише «запит пішов»: форма,
 * що шле не те, що на екрані (або стару версію рядка), зеленіла б на
 * будь-якій перевірці виду «PUT викликано».
 *
 * ⚠ Заглушки відмов — БЕЗ `errorCode`: клієнт розрізняє конфлікт за
 * `status === 409`, тож коду для доказу не потрібно; `problemOf` сам підставить
 * `HTTP-409`. (`ECR-JOB-0409` у `ErrorCodes.cs` уже є — застереження про
 * вигаданий код тут більше не діє.)
 */

const Strings: Record<string, string> = {
  'common.save': 'Save',
  'common.cancel': 'Cancel',
  'common.delete': 'Remove',
  'common.retry': 'Retry',
  'sources.never': 'never',
  'state.errorTitle': 'Error',
  'schedule.none': 'No schedule',
  'schedule.cron': 'Cron expression',
  'schedule.cronHint': 'Quartz format',
  'schedule.enabled': 'Enabled',
  'schedule.lastRun': 'Last run',
  'schedule.notApplied': 'Not scheduled',
  'schedule.create': 'Add schedule',
  'schedule.removeConfirm': 'Remove the schedule?',
  'schedule.reload': 'Load current version',
  'schedule.saved': 'Saved',
  'schedule.removed': 'Removed',
  'schedule.cronEmpty': 'Enter a cron expression',
  'schedule.cronTooLong': 'At most {max} characters',
  'schedule.cronFieldCount': 'Needs 6 or 7 fields, got {count}',
  'schedule.cronField': 'Field {position}: "{value}" is not allowed',
  'schedule.cronDayQuestion': 'Exactly one day field must be ?',
};

const SourceEntityId = 42;

const schedule = (overrides: Partial<CollectionSchedule> = {}): CollectionSchedule => ({
  id: 7,
  sourceEntityId: SourceEntityId,
  sourceEntityCode: 'STACK-1',
  sourceEntityName: 'Stack analyzer',
  dataSourceId: 3,
  dataSourceCode: 'PI-WEST',
  cron: '0 15 2 * * ?',
  isEnabled: true,
  lastRunAt: null,
  lastError: null,
  lastErrorAt: null,
  rowVersion: 'AAAAAAAAB9E=',
  ...overrides,
});

const json = (body: unknown, status = 200, type = 'application/json'): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': type } });

const problem = (status: number, detail: string, messageKey: string): Response =>
  json(
    { title: `Refused ${status}`, status, detail, correlationId: 'c-1', messageKey },
    status,
    'application/problem+json',
  );

interface Call {
  method: string;
  url: string;
  body: unknown;
  ifMatch: string | null;
}

/**
 * Заглушка мережі.
 *
 * @param list Відповіді на `GET /collection-schedules` ПО ЧЕРЗІ; остання
 *   повторюється.
 * @param write Відповідь на запис.
 */
function stub(list: (() => Response)[], write: (call: Call) => Response = () => json({})): Call[] {
  const calls: Call[] = [];
  let reads = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });

      if (url.includes('/api/v1/collection-schedules')) {
        const method = init?.method ?? 'GET';

        if (method === 'GET') {
          const next = list[Math.min(reads, list.length - 1)];
          reads += 1;

          return (next ?? (() => json([])))();
        }

        const call: Call = {
          method,
          url,
          body: typeof init?.body === 'string' ? JSON.parse(init.body) : null,
          ifMatch: new Headers(init?.headers).get('If-Match'),
        };
        calls.push(call);

        return write(call);
      }

      return json([]);
    }),
  );

  return calls;
}

async function show(): Promise<void> {
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  render(
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <CollectionScheduleTab sourceEntityId={SourceEntityId} dataSource="PI-WEST" />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const cronInput = (): HTMLInputElement => screen.getByLabelText(/Cron expression/) as HTMLInputElement;

async function typeCron(value: string): Promise<void> {
  await userEvent.clear(cronInput());
  await userEvent.type(cronInput(), value);
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const Slow = 120_000;

describe('CollectionScheduleTab', () => {
  it(
    'немає розкладу: форма створення, і POST несе рівно те, що на екрані',
    async () => {
      const calls = stub([() => json([schedule({ id: 9, sourceEntityId: 5 })])], () =>
        json(schedule({ id: 11, cron: '0 30 4 ? * MON-FRI', isEnabled: false, rowVersion: 'BBBB' }), 201),
      );
      await show();

      await screen.findByText('No schedule');

      // ⚠ Розклад ІНШОЇ сутності в переліку є — і не показаний як цей.
      expect(cronInput().value).toBe('');

      const create = screen.getByRole('button', { name: 'Add schedule' });
      expect(create).toHaveProperty('disabled', true);
      expect(screen.getByText('Enter a cron expression')).toBeTruthy();

      await typeCron('0 30 4 ? * MON-FRI');
      await userEvent.click(screen.getByLabelText('Enabled'));
      await userEvent.click(create);

      await waitFor(() => expect(calls).toHaveLength(1));
      expect(calls[0]).toEqual({
        method: 'POST',
        url: '/api/v1/collection-schedules',
        body: { sourceEntityId: SourceEntityId, cron: '0 30 4 ? * MON-FRI', isEnabled: false },
        ifMatch: null,
      });

      // Після створення форма стоїть на новому рядку.
      await screen.findByRole('button', { name: 'Save' });
      expect(screen.queryByText('No schedule')).toBeNull();
    },
    Slow,
  );

  it(
    'є розклад: PUT несе значення з екрана і rowVersion показаного рядка',
    async () => {
      const calls = stub([() => json([schedule()])], () =>
        json(schedule({ cron: '0 0 3 * * ?', isEnabled: false, rowVersion: 'CCCC' })),
      );
      await show();

      await waitFor(() => expect(cronInput().value).toBe('0 15 2 * * ?'));
      expect(screen.queryByText('No schedule')).toBeNull();
      expect(screen.getByText('never')).toBeTruthy();

      // Незмінена форма — зберігати нічого.
      expect(screen.getByRole('button', { name: 'Save' })).toHaveProperty('disabled', true);

      await typeCron('0 0 3 * * ?');
      await userEvent.click(screen.getByLabelText('Enabled'));
      await userEvent.click(screen.getByRole('button', { name: 'Save' }));

      await waitFor(() => expect(calls).toHaveLength(1));
      expect(calls[0]).toEqual({
        method: 'PUT',
        url: '/api/v1/collection-schedules/7',
        body: { cron: '0 0 3 * * ?', isEnabled: false },
        ifMatch: '"AAAAAAAAB9E="',
      });
    },
    Slow,
  );

  it(
    'lastError: причина постановки видима, а не лише «розклад є»',
    async () => {
      stub([
        () =>
          json([
            schedule({
              lastError: 'Trigger does not fire: no future fire times',
              lastErrorAt: '2026-09-20T03:00:00Z',
            }),
          ]),
      ]);
      await show();

      const alert = await screen.findByText('Not scheduled');
      const box = alert.closest('[data-last-error]');

      expect(box).not.toBeNull();
      expect(box?.textContent).toContain('Trigger does not fire: no future fire times');
      expect(box?.querySelector('time[datetime="2026-09-20T03:00:00Z"]')).not.toBeNull();
    },
    Slow,
  );

  it(
    'поставлений розклад НЕ показує тривоги',
    async () => {
      stub([() => json([schedule()])]);
      await show();

      await waitFor(() => expect(cronInput().value).toBe('0 15 2 * * ?'));
      expect(screen.queryByText('Not scheduled')).toBeNull();
    },
    Slow,
  );

  it(
    '409: причина видима, збереження заблоковане, і лише перечитування дає нову версію',
    async () => {
      const calls = stub(
        [
          () => json([schedule()]),
          () => json([schedule({ cron: '0 0 5 * * ?', rowVersion: 'DDDD' })]),
        ],
        (call) =>
          call.ifMatch === '"AAAAAAAAB9E="'
            ? problem(
                409,
                'Someone else changed this schedule after you read it.',
                'err.collectionScheduleChanged',
              )
            : json(schedule({ cron: '0 0 6 * * ?', rowVersion: 'EEEE' })),
      );
      await show();

      await waitFor(() => expect(cronInput().value).toBe('0 15 2 * * ?'));
      await typeCron('0 0 4 * * ?');
      await userEvent.click(screen.getByRole('button', { name: 'Save' }));

      await screen.findByText('Someone else changed this schedule after you read it.');

      // ⛔ Не затерто мовчки: чернетка людини лишилась, повтор не пішов сам,
      // і натиснути «Зберегти» поверх конфлікту неможливо.
      expect(cronInput().value).toBe('0 0 4 * * ?');
      expect(screen.getByRole('button', { name: 'Save' })).toHaveProperty('disabled', true);
      expect(calls).toHaveLength(1);

      await userEvent.click(screen.getByRole('button', { name: 'Load current version' }));

      // Свіжий рядок — чужі значення на екрані, причина прибрана.
      await waitFor(() => expect(cronInput().value).toBe('0 0 5 * * ?'));
      expect(screen.queryByText('Someone else changed this schedule after you read it.')).toBeNull();

      await typeCron('0 0 6 * * ?');
      await userEvent.click(screen.getByRole('button', { name: 'Save' }));

      await waitFor(() => expect(calls).toHaveLength(2));
      expect(calls[1]?.ifMatch).toBe('"DDDD"');
      expect(calls[1]?.body).toEqual({ cron: '0 0 6 * * ?', isEnabled: true });
    },
    Slow,
  );

  it(
    'невалідний формат: кнопка недоступна, причина видима до кліку, запиту немає',
    async () => {
      const calls = stub([() => json([schedule()])]);
      await show();

      await waitFor(() => expect(cronInput().value).toBe('0 15 2 * * ?'));

      // Обидва поля дня конкретні — Quartz: «not implemented».
      await typeCron('0 15 2 * * *');

      const save = screen.getByRole('button', { name: 'Save' });
      expect(screen.getByText('Exactly one day field must be ?')).toBeTruthy();
      expect(save).toHaveProperty('disabled', true);

      await userEvent.click(save);
      expect(calls).toHaveLength(0);

      await typeCron('0 60 2 * * ?');
      expect(screen.getByText('Field 2: "60" is not allowed')).toBeTruthy();
      expect(screen.getByRole('button', { name: 'Save' })).toHaveProperty('disabled', true);
    },
    Slow,
  );

  it(
    'відмова читання — це НЕ «розкладу немає»',
    async () => {
      const calls = stub([() => problem(500, 'Database is unreachable.', 'err.internal')]);
      await show();

      await screen.findByText('Database is unreachable.');

      expect(screen.queryByText('No schedule')).toBeNull();
      expect(screen.queryByRole('button', { name: 'Add schedule' })).toBeNull();
      expect(screen.queryByLabelText(/Cron expression/)).toBeNull();
      expect(screen.getByRole('button', { name: 'Retry' })).toBeTruthy();
      expect(calls).toHaveLength(0);
    },
    Slow,
  );

  it(
    'прибирання шле DELETE із версією показаного рядка й лише після підтвердження',
    async () => {
      const calls = stub([() => json([schedule()])], () => new Response(null, { status: 204 }));
      await show();

      await waitFor(() => expect(cronInput().value).toBe('0 15 2 * * ?'));
      await userEvent.click(screen.getByRole('button', { name: 'Remove' }));

      expect(calls).toHaveLength(0);
      screen.getByText('Remove the schedule?');

      await userEvent.click(screen.getByRole('button', { name: 'Remove' }));

      await waitFor(() => expect(calls).toHaveLength(1));
      expect(calls[0]).toEqual({
        method: 'DELETE',
        url: '/api/v1/collection-schedules/7',
        body: null,
        ifMatch: '"AAAAAAAAB9E="',
      });

      await screen.findByText('No schedule');
    },
    Slow,
  );
});
