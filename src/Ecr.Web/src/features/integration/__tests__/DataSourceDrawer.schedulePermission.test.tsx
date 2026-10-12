import { describe, it, expect, vi, afterEach, beforeAll } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { SourcesPage } from '@/pages/admin/SourcesPage';

/**
 * Вкладка «Schedule» шухляди з'єднання й право `Integration.EditSchedule`.
 *
 * ⛔ Сервер вимагає це право на ВСІ три дії розкладів, включно з переліком
 * (`CollectionSchedulesController`). Раніше клієнт його не перевіряв узагалі: вкладка
 * показувалась за `Integration.View`/`Manage`, а без права користувач отримував відмову `403`
 * в `ErrorAlert` замість розкладів. Тепер вкладки без права НЕМАЄ — як сутностей і подій без
 * `Integration.Manage`; під симуляцією перелік читається, а форм змін немає.
 *
 * ⚠ Сервер відтворено відповідями; `scheduleRequests` — доказ, що без права вкладка не пішла
 * за розкладами взагалі.
 */
const Connection = {
  catalog: null,
  code: 'PI-MAIN',
  collectionSchedules: 1,
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
  code: 'STACK-1',
  dataSourceCode: 'PI-MAIN',
  dataSourceId: 7,
  displayName: 'STACK-1 entity',
  entityPath: null,
  id: 42,
  isActive: true,
  lastRun: null,
  oldestGap: null,
  transport: 'PiWebApi',
};

const Schedule = {
  cron: '0 15 2 * * ?',
  dataSourceCode: 'PI-MAIN',
  dataSourceId: 7,
  id: 1,
  isEnabled: true,
  lastError: null,
  lastErrorAt: null,
  lastRunAt: null,
  rowVersion: 'AAAAAAAAB9E=',
  sourceEntityCode: 'STACK-1',
  sourceEntityId: 42,
  sourceEntityName: 'STACK-1 entity',
};

let scheduleRequests: string[] = [];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function respond(permissions: string[], isSimulation = false): void {
  scheduleRequests = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input), 'http://localhost').pathname;

      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation,
          language: 'en',
          mustChangePassword: false,
          permissions,
          simulatedForUserId: isSimulation ? 5 : null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (path.endsWith('/api/v1/collection-schedules')) {
        scheduleRequests.push(path);

        // Сервер: перелік розкладів — лише з `Integration.EditSchedule`.
        return permissions.includes('Integration.EditSchedule')
          ? json([Schedule])
          : json(
              {
                title: 'Access denied',
                status: 403,
                errorCode: 'ECR-AUTH-0403',
                correlationId: 'c-403',
                messageKey: 'err.ECR-AUTH-0403.permission',
              },
              403,
            );
      }

      if (path.endsWith('/api/v1/data-sources')) return json([Connection]);
      if (path.endsWith('/api/v1/sources')) return json([Entity]);

      return json(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/sources?panel=PI-MAIN']}>
          <SourcesPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Шухляда, коли профіль уже доїхав (вкладка Connection намальована з даними з'єднання). */
async function readyDrawer(): Promise<HTMLElement> {
  const drawer = await screen.findByRole('dialog');
  await within(drawer).findByText('https://pi.example.invalid/piwebapi');

  return drawer;
}

beforeAll(async () => {
  await import('@/features/integration/DataSourceScheduleTab');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("Шухляда з'єднання: вкладка Schedule і Integration.EditSchedule", () => {
  it('Manage без EditSchedule — вкладки немає, розклади не читаються', async () => {
    respond(['Integration.View', 'Integration.Manage']);
    show();

    const drawer = await readyDrawer();

    // Інші вкладки на місці — отже, шухляда повністю намальована, а відсутність саме «Schedule» правдива.
    await within(drawer).findByRole('tab', { name: /sources\.tabEntities/ });
    expect(within(drawer).queryByRole('tab', { name: /sources\.tabSchedule/ })).toBeNull();
    expect(drawer.querySelector('[data-schedule-tab]')).toBeNull();
    expect(scheduleRequests).toEqual([]);
  });

  it('лише View — теж без вкладки «Schedule»', async () => {
    respond(['Integration.View']);
    show();

    const drawer = await readyDrawer();

    expect(within(drawer).queryByRole('tab', { name: /sources\.tabSchedule/ })).toBeNull();
    expect(scheduleRequests).toEqual([]);
  });

  it('з EditSchedule — вкладка є, перелік розкладів і вибір сутності для правки', async () => {
    respond(['Integration.View', 'Integration.Manage', 'Integration.EditSchedule']);
    show();

    const drawer = await readyDrawer();
    fireEvent.click(await within(drawer).findByRole('tab', { name: /sources\.tabSchedule/ }));

    await waitFor(() => expect(drawer.querySelector('[data-schedule-row="STACK-1"]')).not.toBeNull());
    expect(within(drawer).getByRole('textbox', { name: /sources\.scheduleEntity/ })).toBeTruthy();
    expect(within(drawer).getByRole('button', { name: 'STACK-1 entity' })).toBeTruthy();
  });

  it('EditSchedule без Manage — вкладка «Schedule» є, а сутностей і подій немає', async () => {
    respond(['Integration.View', 'Integration.EditSchedule']);
    show();

    const drawer = await readyDrawer();

    await within(drawer).findByRole('tab', { name: /sources\.tabSchedule/ });
    expect(within(drawer).queryByRole('tab', { name: /sources\.tabEntities/ })).toBeNull();
    expect(within(drawer).queryByRole('tab', { name: /sourceEvents\.tab/ })).toBeNull();
  });

  it('симуляція: перелік розкладів читається, а вибору сутності й форм змін немає', async () => {
    respond(['Integration.View', 'Integration.Manage', 'Integration.EditSchedule'], true);
    show();

    const drawer = await readyDrawer();
    fireEvent.click(await within(drawer).findByRole('tab', { name: /sources\.tabSchedule/ }));

    await waitFor(() => expect(drawer.querySelector('[data-schedule-row="STACK-1"]')).not.toBeNull());

    // Назва сутності — текст, а не кнопка, що відкривала б форму зміни.
    expect(within(drawer).queryByRole('button', { name: 'STACK-1 entity' })).toBeNull();
    expect(within(drawer).queryByRole('textbox', { name: /sources\.scheduleEntity/ })).toBeNull();
    expect(drawer.querySelector('[data-collection-schedule]')).toBeNull();
  });
});
