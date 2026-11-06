import { describe, it, expect, afterEach, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { withTestDefaults } from '@/test/render';
import type { CollectionSchedule } from '@/features/integration/scheduleApi';
import { SourceEventFrequency } from '../SourceEventFrequency';
import { cronOfFrequency, EventFrequencies, frequencyOfCron } from '../eventFrequency';

/**
 * Q-2 (людина 06.11): частота синку подій PI — налаштування в UI. Перевіряється ТІЛО запиту: розкладу немає — POST
 * увімкненого розкладу з вибраним cron; є — PUT лише cron зі збереженим станом/вікном і `If-Match` показаної версії.
 */

const Strings: Record<string, string> = {
  'sourceEvents.frequency.label': 'Event sync frequency',
  'sourceEvents.frequency.hint': 'How often',
  'sourceEvents.frequency.save': 'Save frequency',
  'sourceEvents.frequency.none': 'No schedule yet',
  'sourceEvents.frequency.custom': 'Custom ({cron})',
  'sourceEvents.frequency.m15': 'Every 15 minutes',
  'sourceEvents.frequency.h1': 'Every hour',
  'sourceEvents.frequency.h6': 'Every 6 hours',
  'schedule.saved': 'Saved',
};

const EntityId = 42;

const schedule = (overrides: Partial<CollectionSchedule> = {}): CollectionSchedule => ({
  id: 7,
  sourceEntityId: EntityId,
  sourceEntityCode: 'Flare_Auto',
  sourceEntityName: null,
  dataSourceId: 3,
  dataSourceCode: 'PI-FLERT',
  cron: '0 0 * * * ?',
  isEnabled: true,
  lastRunAt: null,
  lastError: null,
  lastErrorAt: null,
  rowVersion: 'AAAAAAAAB9E=',
  lookbackDays: 3,
  ...overrides,
});

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

interface Call {
  method: string;
  url: string;
  body: unknown;
  ifMatch: string | null;
}

function stub(rows: CollectionSchedule[], saved: CollectionSchedule): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });
      if (url.includes('/api/v1/collection-schedules')) {
        const method = init?.method ?? 'GET';
        if (method === 'GET') return json(rows);
        calls.push({
          method,
          url,
          body: typeof init?.body === 'string' ? JSON.parse(init.body) : null,
          ifMatch: new Headers(init?.headers).get('If-Match'),
        });
        return json(saved, method === 'POST' ? 201 : 200);
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
        <SourceEventFrequency sourceEntityId={EntityId} dataSource="PI-FLERT" />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function choose(label: string): Promise<void> {
  fireEvent.click(await screen.findByRole('textbox', { name: 'Event sync frequency' }));
  fireEvent.click(await screen.findByRole('option', { name: label }));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('eventFrequency', () => {
  it('набір частот 5 хв … 24 год і зворотне читання cron (пробіли не важать)', () => {
    expect(EventFrequencies.map((item) => item.value)).toEqual([
      'PT5M', 'PT15M', 'PT30M', 'PT1H', 'PT2H', 'PT6H', 'PT12H', 'P1D',
    ]);
    expect(frequencyOfCron(' 0  0 * * * ? ')).toBe('PT1H');
    expect(frequencyOfCron('0 15 2 * * ?')).toBeNull();
    expect(cronOfFrequency('PT15M')).toBe('0 0/15 * * * ?');
  });
});

describe('SourceEventFrequency', () => {
  it('розкладу немає: типово «щогодини» з підказкою, а збереження створює увімкнений розклад з вибраним cron', async () => {
    const calls = stub([schedule({ id: 9, sourceEntityId: 5 })], schedule({ id: 11, cron: '0 0/15 * * * ?' }));
    await show();

    expect(await screen.findByText('No schedule yet')).toBeTruthy();
    expect((screen.getByRole('textbox', { name: 'Event sync frequency' }) as HTMLInputElement).value).toBe('Every hour');

    await choose('Every 15 minutes');
    fireEvent.click(screen.getByRole('button', { name: 'Save frequency' }));

    await waitFor(() => expect(calls).toHaveLength(1));
    expect(calls[0]).toEqual({
      method: 'POST',
      url: '/api/v1/collection-schedules',
      body: { sourceEntityId: EntityId, cron: '0 0/15 * * * ?', isEnabled: true },
      ifMatch: null,
    });
  });

  it('розклад є: змінює лише cron, зберігає стан і вікно, If-Match — показана версія', async () => {
    const calls = stub([schedule()], schedule({ cron: '0 0 0/6 * * ?', rowVersion: 'CCCC' }));
    await show();

    const save = await screen.findByRole('button', { name: 'Save frequency' });
    expect(save).toHaveProperty('disabled', true);

    await choose('Every 6 hours');
    fireEvent.click(save);

    await waitFor(() => expect(calls).toHaveLength(1));
    expect(calls[0]).toEqual({
      method: 'PUT',
      url: '/api/v1/collection-schedules/7',
      body: { cron: '0 0 0/6 * * ?', isEnabled: true, lookbackDays: 3 },
      ifMatch: '"AAAAAAAAB9E="',
    });
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save frequency' })).toHaveProperty('disabled', true));
  });

  it('cron поза набором показується як «Інший» і не затирається без вибору', async () => {
    stub([schedule({ cron: '0 15 2 * * ?' })], schedule());
    await show();

    const input = (await screen.findByRole('textbox', { name: 'Event sync frequency' })) as HTMLInputElement;
    expect(input.value).toBe('Custom (0 15 2 * * ?)');
    expect(screen.getByRole('button', { name: 'Save frequency' })).toHaveProperty('disabled', true);
  });
});
