import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SourceEntitiesTab } from '@/features/integration/SourceEntitiesTab';
import type { DataSource } from '@/features/integration/dataSourceApi';
import { testTheme } from '@/test/render';

/**
 * Вкладка «Entities» шухляди з'єднання: стани «помилка», «немає права»,
 * «порожньо», «завантаження» (`ФВ-14.22`) переліку сутностей, плюс відмова
 * довідників (вибір прив'язки без них — не «довідників немає»).
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
      JSON.stringify({
        title: status === 403 ? 'Forbidden' : 'Server error',
        status,
        errorCode,
        correlationId: 'corr-entities',
        detail: null,
      }),
      { status, headers: { 'Content-Type': 'application/problem+json' } },
    ),
  );

function serve(entities: () => Promise<Response>, registries: () => Promise<Response> = () => ok([])): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input), 'http://localhost').pathname;

      if (path === '/api/v1/sources') return entities();
      if (path === '/api/v1/registries') return registries();
      if (path === '/api/v1/me') {
        return ok({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Integration.View', 'Integration.Manage'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'me',
        });
      }

      return ok(null);
    }),
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <SourceEntitiesTab source={Source} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SourceEntitiesTab — стани', () => {
  it('500: показує помилку з кодом, а не «сутностей немає»', async () => {
    serve(() => problem(500, 'ECR-SYS-0500'));
    show();

    expect(await screen.findByText('ECR-SYS-0500', { exact: false })).toBeTruthy();
    expect(document.querySelector('[data-entities-empty]')).toBeNull();
  });

  it('403: відмову видно кодом, а не «сутностей немає»', async () => {
    serve(() => problem(403, 'ECR-AUTH-0403'));
    show();

    expect(await screen.findByText('ECR-AUTH-0403', { exact: false })).toBeTruthy();
    expect(document.querySelector('[data-entities-empty]')).toBeNull();
  });

  it('відмова довідників при наявних сутностях: помилку видно, вибір прив\'язки вимкнено', async () => {
    serve(
      () =>
        ok([
          {
            code: 'STACK-1',
            dataSourceCode: 'PI-MAIN',
            dataSourceId: 7,
            displayName: 'Stack 1',
            entityPath: null,
            id: 42,
            isActive: true,
            lastRun: null,
            oldestGap: null,
            registryDefId: null,
            transport: 'PiWebApi',
          },
        ]),
      () => problem(500, 'ECR-SYS-0500'),
    );
    show();

    expect(await screen.findByText('ECR-SYS-0500', { exact: false })).toBeTruthy();
    const select = document.querySelector<HTMLInputElement>('[data-entity-registry="STACK-1"]');
    expect(select?.disabled).toBe(true);
  });

  it('порожньо: «сутностей немає» і жодної таблиці', async () => {
    serve(() => ok([]));
    show();

    expect(await screen.findByText('⟦sources.scheduleNoEntities⟧')).toBeTruthy();
    expect(document.querySelector('[data-entities]')).toBeNull();
  });

  it('у дорозі: видно завантаження, а не «сутностей немає»', async () => {
    serve(() => new Promise<Response>(() => undefined));
    show();

    await vi.waitFor(() => expect(document.querySelector('.mantine-Loader-root')).toBeTruthy());
    expect(document.querySelector('[data-entities-empty]')).toBeNull();
    expect(document.querySelector('[data-entities-tab]')).toBeNull();
  });
});
