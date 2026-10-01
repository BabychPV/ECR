import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DataSourceScheduleTab } from '@/features/integration/DataSourceScheduleTab';
import type { DataSource } from '@/features/integration/dataSourceApi';
import { testTheme } from '@/test/render';

/**
 * Вкладка «Schedule» шухляди з'єднання: стани «немає права», «порожньо»,
 * «завантаження» (`ФВ-14.22`). Відмову 500 розкладів уже тримає
 * `DataSourceScheduleTab.test.tsx` («L10»); тут — 403 обох запитів (розклади
 * й сутності), порожньо і запит у дорозі.
 */
configure({ asyncUtilTimeout: 10_000 });

const Source: DataSource = {
  catalog: null,
  code: 'PI-MAIN',
  collectionSchedules: 0,
  endpoint: 'https://pi-main.example.invalid/api',
  hasSecret: false,
  id: 7,
  isActive: true,
  maxParallel: 4,
  nameL10n: { en: 'Main PI server' },
  rowVersion: 'AAAAAAAAB9E=',
  secondaryEndpoint: null,
  sourceEntities: 0,
  transport: 'PiWebApi',
};

const ok = (body: unknown): Promise<Response> =>
  Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }));

const problem = (status: number, errorCode: string): Promise<Response> =>
  Promise.resolve(
    new Response(
      JSON.stringify({ title: 'Forbidden', status, errorCode, correlationId: 'corr-schedules', detail: null }),
      { status, headers: { 'Content-Type': 'application/problem+json' } },
    ),
  );

function serve(schedules: () => Promise<Response>, entities: () => Promise<Response> = () => ok([])): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input), 'http://localhost').pathname;

      if (path === '/api/v1/collection-schedules') return schedules();
      if (path === '/api/v1/sources') return entities();

      return ok(null);
    }),
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <DataSourceScheduleTab source={Source} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DataSourceScheduleTab — стани', () => {
  it('403 розкладів: відмову видно кодом, а не «розкладів немає»', async () => {
    serve(() => problem(403, 'ECR-AUTH-0403'));
    show();

    expect(await screen.findByText('ECR-AUTH-0403', { exact: false })).toBeTruthy();
    expect(document.querySelector('[data-schedules-empty]')).toBeNull();
  });

  it('403 сутностей: відмову видно кодом, а не «сутностей немає»', async () => {
    serve(() => ok([]), () => problem(403, 'ECR-AUTH-0403'));
    show();

    expect(await screen.findByText('ECR-AUTH-0403', { exact: false })).toBeTruthy();
    expect(screen.queryByText('⟦sources.scheduleNoEntities⟧')).toBeNull();
    expect(document.querySelector('[data-schedules-empty]')).toBeNull();
  });

  it('порожньо: «розкладів немає» і «сутностей немає», жодної таблиці', async () => {
    serve(() => ok([]));
    show();

    expect(await screen.findByText('⟦sources.schedulesNone⟧')).toBeTruthy();
    expect(screen.queryByText('⟦sources.scheduleNoEntities⟧')).toBeTruthy();
    expect(document.querySelector('[data-schedules]')).toBeNull();
  });

  it('у дорозі: видно завантаження, а не «розкладів немає»', async () => {
    serve(() => new Promise<Response>(() => undefined));
    show();

    // Вкладка поки малює лише `Loader` — чекаємо саме його.
    await vi.waitFor(() => expect(document.querySelector('.mantine-Loader-root')).toBeTruthy());
    expect(document.querySelector('[data-schedules-empty]')).toBeNull();
    expect(screen.queryByText('⟦sources.scheduleNoEntities⟧')).toBeNull();
  });
});
