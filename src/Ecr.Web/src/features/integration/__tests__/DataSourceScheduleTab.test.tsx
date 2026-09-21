import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { JSX } from 'react';
import { SourcesPage } from '@/pages/admin/SourcesPage';
import { useCollectionSchedules } from '@/features/integration/useCollectionSchedules';

/**
 * Вкладка «Schedule» шухляди з'єднання (`UI-09`).
 *
 * ⛔ Запит ловиться на рівні `fetch` разом із рядком запиту: доказ у тому, що
 * на сервер пішов саме фільтр `?dataSource=<код з'єднання>`, а не що
 * вкладка відфільтрувала чужі рядки в себе.
 */
function connection(code: string, id: number, name: string): Record<string, unknown> {
  return {
    catalog: null,
    code,
    collectionSchedules: 1,
    endpoint: `https://${code.toLowerCase()}.example.invalid/api`,
    hasSecret: false,
    id,
    isActive: true,
    maxParallel: 4,
    nameL10n: { en: name },
    rowVersion: 'AAAAAAAAB9E=',
    secondaryEndpoint: null,
    sourceEntities: 1,
    transport: 'PiWebApi',
  };
}

function entity(id: number, code: string, dataSourceCode: string, dataSourceId: number): Record<string, unknown> {
  return {
    code,
    dataSourceCode,
    dataSourceId,
    displayName: `${code} entity`,
    entityPath: null,
    id,
    isActive: true,
    lastRun: null,
    oldestGap: null,
    transport: 'PiWebApi',
  };
}

function scheduleOf(id: number, sourceEntityId: number, code: string, dataSourceCode: string): Record<string, unknown> {
  return {
    cron: '0 15 2 * * ?',
    dataSourceCode,
    dataSourceId: dataSourceCode === 'PI-MAIN' ? 7 : 8,
    id,
    isEnabled: true,
    lastError: null,
    lastErrorAt: null,
    lastRunAt: null,
    rowVersion: 'AAAAAAAAB9E=',
    sourceEntityCode: code,
    sourceEntityId,
    sourceEntityName: `${code} entity`,
  };
}

/** Розклади, які сервер віддає на `?dataSource=<код>`; без фільтра — усі. */
const SchedulesBySource: Record<string, Record<string, unknown>[]> = {
  'PI-MAIN': [scheduleOf(1, 42, 'STACK-1', 'PI-MAIN')],
  LAB: [scheduleOf(2, 43, 'LAB-1', 'LAB')],
};

let scheduleUrls: string[] = [];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function respond(schedules: (url: URL) => Response = byFilter): void {
  scheduleUrls = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), 'http://localhost');
      const path = url.pathname;

      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Integration.View', 'Integration.Manage'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (path.endsWith('/api/v1/collection-schedules')) {
        scheduleUrls.push(`${path}${url.search}`);

        return schedules(url);
      }

      if (path.endsWith('/api/v1/data-sources')) {
        return json([connection('PI-MAIN', 7, 'Main PI server'), connection('LAB', 8, 'Lab feed')]);
      }

      if (path.endsWith('/api/v1/sources')) {
        return json([entity(42, 'STACK-1', 'PI-MAIN', 7), entity(43, 'LAB-1', 'LAB', 8)]);
      }

      return json(null);
    }),
  );
}

function byFilter(url: URL): Response {
  const code = url.searchParams.get('dataSource');

  return json(code === null ? Object.values(SchedulesBySource).flat() : (SchedulesBySource[code] ?? []));
}

function show(entry = '/admin/sources?panel=PI-MAIN'): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[entry]}>
          <SourcesPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function openScheduleTab(): Promise<HTMLElement> {
  const drawer = await screen.findByRole('dialog');
  fireEvent.click(await within(drawer).findByRole('tab', { name: /sources\.tabSchedule/ }));

  return drawer;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("Шухляда з'єднання: вкладка Schedule", () => {
  it('L2: закрита за замовчуванням — розклади не читаються, доки вкладку не відкрили', async () => {
    respond();
    show();

    const drawer = await screen.findByRole('dialog');
    const tab = await within(drawer).findByRole('tab', { name: /sources\.tabSchedule/ });

    // Відкрита саме Connection: її вміст на екрані, вкладка розкладу не обрана.
    expect(await within(drawer).findByText('https://pi-main.example.invalid/api')).toBeTruthy();
    expect(tab.getAttribute('aria-selected')).toBe('false');

    expect(drawer.querySelector('[data-schedule-tab]')).toBeNull();
    expect(scheduleUrls).toEqual([]);
  });

  it('читає розклади з фільтром ?dataSource=<код з\'єднання> і показує саме їх', async () => {
    respond();
    show();

    const drawer = await openScheduleTab();

    await waitFor(() => expect(drawer.querySelector('[data-schedule-row="STACK-1"]')).not.toBeNull());
    expect(drawer.querySelector('[data-schedule-row="LAB-1"]')).toBeNull();

    expect(scheduleUrls.length).toBeGreaterThan(0);
    expect(new Set(scheduleUrls)).toEqual(new Set(['/api/v1/collection-schedules?dataSource=PI-MAIN']));
  });

  it('L10: відмова читання — ErrorAlert, а не «розкладів немає»', async () => {
    respond(() => json({ title: 'Server error', status: 500, correlationId: 'c-500' }, 500));
    show();

    const drawer = await openScheduleTab();

    expect(await within(drawer).findByRole('alert')).toBeTruthy();
    expect(drawer.querySelector('[data-schedules-empty]')).toBeNull();
    expect(drawer.querySelector('[data-schedules]')).toBeNull();
  });

  it('вибір сутності з переліку відкриває редактор її розкладу', async () => {
    respond();
    show();

    const drawer = await openScheduleTab();

    fireEvent.click(await within(drawer).findByRole('button', { name: 'STACK-1 entity' }));

    const cron = (await within(drawer).findByLabelText(/schedule\.cron/, {
      selector: 'input',
    })) as HTMLInputElement;
    expect(cron.value).toBe('0 15 2 * * ?');
  });

  it('у виборі сутностей — лише сутності цього з\'єднання', async () => {
    respond();
    show();

    const drawer = await openScheduleTab();

    fireEvent.click(await within(drawer).findByRole('textbox', { name: /sources\.scheduleEntity/ }));

    const options = await screen.findAllByRole('option');
    expect(options.map((option) => option.textContent)).toEqual(['STACK-1 entity']);
  });
});

/** Показує коди сутностей із розкладів одного з'єднання. */
function Probe({ code }: { readonly code: string }): JSX.Element {
  const schedules = useCollectionSchedules(code);

  return (
    <output data-testid={code}>
      {(schedules.data ?? []).map((row) => row.sourceEntityCode).join(',')}
    </output>
  );
}

describe('useCollectionSchedules: кеш за кодом з\'єднання', () => {
  it('два з\'єднання одночасно — два запити й два різні переліки', async () => {
    respond();

    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <Probe code="PI-MAIN" />
        <Probe code="LAB" />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(screen.getByTestId('PI-MAIN').textContent).toBe('STACK-1'));
    await waitFor(() => expect(screen.getByTestId('LAB').textContent).toBe('LAB-1'));

    expect(new Set(scheduleUrls)).toEqual(
      new Set([
        '/api/v1/collection-schedules?dataSource=PI-MAIN',
        '/api/v1/collection-schedules?dataSource=LAB',
      ]),
    );
  });
});
