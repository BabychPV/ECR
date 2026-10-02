import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CollectionScheduleTab } from '@/features/integration/CollectionScheduleTab';
import { DataSourceScheduleTab } from '@/features/integration/DataSourceScheduleTab';
import type { DataSource } from '@/features/integration/dataSourceApi';
import type { CollectionSchedule } from '@/features/integration/scheduleApi';
import { testTheme } from '@/test/render';

/**
 * Залежності розкладів (`ФВ-13.15`) — стани й доступні імена, яких не тримають основні файли.
 *
 * Мутаційні докази (лише локально): без `aria-label` у `Loader` — очікування не знаходиться за
 * роллю й ім'ям; без опції `''` у переліку — зняти залежність без хрестика нема чим; без гілки `#id` у переліку —
 * зникла ціль читається як «—», тобто «без залежності».
 */

const row = (overrides: Partial<CollectionSchedule> = {}): CollectionSchedule => ({
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
  dependsOnScheduleId: null,
  ...overrides,
});

const Source: DataSource = {
  catalog: null,
  code: 'PI-WEST',
  collectionSchedules: 2,
  endpoint: 'https://pi-west.example.invalid/api',
  hasSecret: false,
  id: 3,
  isActive: true,
  maxParallel: 4,
  nameL10n: { en: 'West PI' },
  rowVersion: 'AAAAAAAAB9E=',
  secondaryEndpoint: null,
  sourceEntities: 0,
  transport: 'PiWebApi',
};

const ok = (body: unknown): Promise<Response> =>
  Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }));

/** Заглушка мережі; повертає тіла записів (`PUT`/`POST`) розкладу. */
function serve(schedules: () => Promise<Response>): unknown[] {
  const writes: unknown[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = new URL(String(input), 'http://localhost').pathname;

      if (path.startsWith('/api/v1/collection-schedules') && (init?.method ?? 'GET') !== 'GET') {
        writes.push(typeof init?.body === 'string' ? JSON.parse(init.body) : null);

        return ok(row());
      }
      if (path === '/api/v1/collection-schedules') return schedules();

      return ok([]);
    }),
  );

  return writes;
}

function mount(node: JSX.Element): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        {node}
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Розклад збору — стани й імена (ФВ-13.15)', () => {
  it('редактор у дорозі: очікування з роллю status і ім\'ям, форми ще немає', async () => {
    serve(() => new Promise<Response>(() => undefined));
    mount(<CollectionScheduleTab sourceEntityId={42} dataSource="PI-WEST" />);

    expect(await screen.findByRole('status', { name: '⟦common.loading⟧' })).toBeDefined();
    expect(screen.queryByText('⟦schedule.none⟧')).toBeNull();
    expect(screen.queryByLabelText(/schedule\.dependsOn⟧/)).toBeNull();
  });

  it('перелік у дорозі: очікування з ім\'ям, а не «розкладів немає»', async () => {
    serve(() => new Promise<Response>(() => undefined));
    mount(<DataSourceScheduleTab source={Source} />);

    expect(await screen.findByRole('status', { name: '⟦common.loading⟧' })).toBeDefined();
    expect(screen.queryByText('⟦sources.schedulesNone⟧')).toBeNull();
  });

  it('наявну залежність знімає опція «без залежності» з переліку — шлях із клавіатури, без хрестика', async () => {
    const other = row({ id: 9, sourceEntityId: 5, sourceEntityCode: 'OTHER-1', sourceEntityName: 'Other entity' });
    const writes = serve(() => ok([row({ dependsOnScheduleId: 9 }), other]));
    mount(<CollectionScheduleTab sourceEntityId={42} dataSource="PI-WEST" />);

    const select = (await screen.findByRole('textbox', { name: /schedule\.dependsOn⟧/ })) as HTMLInputElement;
    expect(select.value).toBe('Other entity');

    await userEvent.click(select);
    await userEvent.click(await screen.findByRole('option', { name: '⟦schedule.dependsOnNone⟧' }));
    expect(select.value).toBe('');
    await userEvent.click(screen.getByRole('button', { name: '⟦common.save⟧' }));

    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]).toEqual({ cron: '0 15 2 * * ?', isEnabled: true, lookbackDays: 7, clearDependency: true });
  });

  it('ціль залежності зникла з переліку — видно її номер, а не «—» («без залежності»)', async () => {
    serve(() => ok([row({ dependsOnScheduleId: 77 }), row({ id: 8, sourceEntityId: 6, sourceEntityCode: 'FREE-1', sourceEntityName: null })]));
    mount(<DataSourceScheduleTab source={Source} />);

    const cells = await screen.findAllByRole('cell');
    const dependency = (code: string): string =>
      document.querySelector(`[data-schedule-row="${code}"] [data-schedule-depends-on]`)?.textContent ?? '';

    expect(cells.length).toBeGreaterThan(0);
    expect(dependency('STACK-1')).toBe('#77');
    expect(dependency('FREE-1')).toBe('—');
  });
});
