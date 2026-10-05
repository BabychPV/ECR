import { describe, it, expect, afterEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { withTestDefaults } from '@/test/render';
import { CollectionScheduleTab } from '@/features/integration/CollectionScheduleTab';
import { DataSourcesQueryKey } from '@/features/integration/dataSourcesKey';
import type { CollectionSchedule } from '@/features/integration/scheduleApi';

/**
 * Аудит L9-30: створення й видалення розкладу скидають кеш з'єднань (`DataSourcesQueryKey`).
 *
 * ⛔ Лічильник `collectionSchedules` з'єднання вирішує в шухляді «у використанні» (кнопка
 * «Видалити» вимкнена з причиною). Без інвалідації він лишався старим до перезавантаження.
 * Правка наявного розкладу лічильника не змінює — і кеш з'єднань не чіпає (контроль).
 *
 * ⚠ Перевіряється стан запису кешу (`isInvalidated`): таблиці з'єднань у тесті немає.
 */
const Strings: Record<string, string> = {
  'common.save': 'Save',
  'common.delete': 'Remove',
  'schedule.cron': 'Cron expression',
  'schedule.create': 'Add schedule',
  'schedule.none': 'No schedule',
  'schedule.enabled': 'Enabled',
  'schedule.removeConfirm': 'Remove the schedule?',
};

const schedule = (overrides: Partial<CollectionSchedule> = {}): CollectionSchedule => ({
  id: 7,
  sourceEntityId: 42,
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
  lookbackDays: 7,
  ...overrides,
});

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

function stub(existing: CollectionSchedule[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });
      if (url.includes('/api/v1/collection-schedules')) {
        if (method === 'GET') return json(existing);
        if (method === 'DELETE') return new Response(null, { status: 204 });
        if (method === 'POST') return json(schedule({ id: 11, cron: '0 30 4 * * ?', rowVersion: 'BBBB' }), 201);

        return json(schedule({ cron: '0 0 3 * * ?', rowVersion: 'CCCC' }));
      }

      return json([]);
    }),
  );
}

async function show(): Promise<QueryClient> {
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(DataSourcesQueryKey, [{ id: 3, code: 'PI-WEST', collectionSchedules: 1, sourceEntities: 1 }]);

  render(
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={client}>
        <CollectionScheduleTab sourceEntityId={42} dataSource="PI-WEST" />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return client;
}

const sourcesInvalidated = (client: QueryClient): boolean =>
  client.getQueryState(DataSourcesQueryKey)?.isInvalidated === true;

const cronInput = (): HTMLInputElement => screen.getByLabelText(/Cron expression/) as HTMLInputElement;

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CollectionScheduleTab: L9-30 — лічильник розкладів з\'єднання', () => {
  it('створення розкладу скидає кеш з\'єднань', async () => {
    stub([]);
    const client = await show();

    await screen.findByText('No schedule');
    await userEvent.type(cronInput(), '0 30 4 * * ?');
    await userEvent.click(screen.getByRole('button', { name: 'Add schedule' }));

    await waitFor(() => expect(sourcesInvalidated(client)).toBe(true));
  }, 60_000);

  it('видалення розкладу скидає кеш з\'єднань', async () => {
    stub([schedule()]);
    const client = await show();

    await waitFor(() => expect(cronInput().value).toBe('0 15 2 * * ?'));
    await userEvent.click(screen.getByRole('button', { name: 'Remove' }));
    await screen.findByText('Remove the schedule?');
    await userEvent.click(screen.getByRole('button', { name: 'Remove' }));

    await waitFor(() => expect(sourcesInvalidated(client)).toBe(true));
  }, 60_000);

  it('правка наявного розкладу кеш з\'єднань не чіпає (контроль)', async () => {
    stub([schedule()]);
    const client = await show();

    await waitFor(() => expect(cronInput().value).toBe('0 15 2 * * ?'));
    await userEvent.clear(cronInput());
    await userEvent.type(cronInput(), '0 0 3 * * ?');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    // Збережене стало новою точкою відліку — форма знову «без змін».
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).toHaveProperty('disabled', true));
    expect(sourcesInvalidated(client)).toBe(false);
  }, 60_000);
});
