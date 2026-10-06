import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { SourcesPage } from '@/pages/admin/SourcesPage';

/**
 * L9-18 (джерела): під симуляцією «очима користувача» сервер відхиляє кожен
 * не-GET (`ECR-SIM-0403`). Перегляд лишається — вкладки сутностей і подій на
 * місці, — а керування (нове з'єднання, правка, проба, видалення, «Додати
 * сутність», прив'язка до довідника) сховане або вимкнене, з банером.
 */
const Connection = {
  catalog: 'ProdAF',
  code: 'PI-MAIN',
  collectionSchedules: 0,
  endpoint: 'https://pi.example.invalid/piwebapi',
  hasSecret: false,
  id: 7,
  isActive: true,
  maxParallel: 4,
  nameL10n: { en: 'Main PI server' },
  rowVersion: 'AAAAAAAAB9E=',
  secondaryEndpoint: null,
  sourceEntities: 1,
  transport: 'PiWebApi',
};

const Entity = {
  id: 42,
  code: 'FLARE-1',
  displayName: 'Flare 1',
  dataSourceCode: 'PI-MAIN',
  dataSourceId: 7,
  isActive: true,
  lastRun: null,
  oldestGap: null,
  transport: 'PiWebApi',
  entityPath: '\\\\AF\\Flare1',
  registryDefId: null,
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function respond(simulating: boolean): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input), 'http://localhost').pathname;

      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: simulating,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Integration.View', 'Integration.Manage'],
          simulatedForUserId: simulating ? 17 : null,
          userId: 1,
          userName: 'tester',
        });
      }
      if (path.endsWith('/api/v1/data-sources')) return json([Connection]);
      if (path.endsWith('/api/v1/sources')) return json([Entity]);
      if (path.endsWith('/api/v1/registries')) return json([]);

      return json(null);
    }),
  );
}

function show(): void {
  render(
    <MantineProvider>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={['/admin/sources?panel=PI-MAIN']}>
          <SourcesPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function openEntities(): Promise<void> {
  fireEvent.click(await screen.findByRole('tab', { name: '⟦sources.tabEntities⟧' }));
  await waitFor(() => expect(document.querySelector('[data-entity-row="FLARE-1"]')).not.toBeNull());
}

/**
 * ⛔ Прогрів спільного модуля дат — та сама причина зависання гейта `client`, що й у
 * `SourceEventsTab.lazy.test.tsx`: шухляда джерела тягне поля дат через
 * `lazy(import('@/shared/dates/DateInputWithStyles'))`, і клік по вкладці (синхронний `act`), що застає цей
 * `import()` незавершеним, крутить рендер без кінця (A1-02, 2026-10-06: воркер на 100 % CPU на цьому файлі,
 * гейт падав кодом 134). Модулі під перевіркою не прогріваються.
 */
beforeAll(async () => {
  await import('@/shared/dates/DateInputWithStyles');
}, 60_000);

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('SourcesPage під симуляцією (L9-18)', () => {
  it('перегляд лишається, керування сховане, прив\'язка вимкнена, є банер', async () => {
    respond(true);
    show();

    expect(await screen.findByRole('tab', { name: '⟦sourceEvents.tab⟧' })).toBeTruthy();
    await openEntities();

    expect(document.querySelector('[data-sources-simulation-read-only]')).not.toBeNull();
    expect(document.querySelector('[data-new-connection]')).toBeNull();
    expect(document.querySelector('[data-edit-connection]')).toBeNull();
    expect(document.querySelector('[data-test-connection]')).toBeNull();
    expect(document.querySelector('[data-delete-connection]')).toBeNull();
    expect(document.querySelector('[data-add-entity-open]')).toBeNull();
    expect(document.querySelector<HTMLInputElement>('[data-entity-registry="FLARE-1"]')?.disabled).toBe(true);
  });

  it('поза симуляцією — керування на місці (контроль)', async () => {
    respond(false);
    show();

    await openEntities();

    expect(document.querySelector('[data-sources-simulation-read-only]')).toBeNull();
    expect(document.querySelector('[data-new-connection]')).not.toBeNull();
    expect(document.querySelector('[data-edit-connection]')).not.toBeNull();
    expect(document.querySelector('[data-add-entity-open]')).not.toBeNull();
    // Довідники вантажаться окремим запитом — до відповіді вибір вимкнений і поза симуляцією.
    await waitFor(() =>
      expect(document.querySelector<HTMLInputElement>('[data-entity-registry="FLARE-1"]')?.disabled).toBe(false),
    );
  });
});
