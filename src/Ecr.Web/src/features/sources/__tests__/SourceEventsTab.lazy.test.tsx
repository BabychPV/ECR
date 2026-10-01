import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { testTheme } from '@/test/render';

/**
 * Лінивість «Подій з PI» (HSE301 A6-UI, бюджет `D-132`): код вкладки не обчислюється, доки вкладку не відкрили,
 * а форма мапінгу — доки не натиснули «Створити мапінг».
 *
 * ⛔ Чим ловиться. Фабрика `vi.mock` виконується, коли модуль ІМПОРТУЮТЬ уперше; вона лише ставить позначку й
 * віддає справжній модуль. Статичний імпорт таблиці подій чи форми з шухляди обчислив би їх разом зі сторінкою
 * джерел — і перша перевірка нижче почервоніла б. Той самий прийом, що в `MyTasksLauncher.test.tsx`; `fireEvent`,
 * а не `userEvent`, з тієї ж причини, що там.
 *
 * ⛔ Порядок перевірок значущий: модуль обчислюється один раз на файл.
 */
const probe = vi.hoisted(() => ({ table: false, modal: false, rowWindow: false }));

// HSE301 A1: розділ прив'язок вікна рядка — теж за `import()`, лише після відкриття вкладки.
vi.mock('@/features/sources/RowWindowMapsPanel', async (importOriginal) => {
  probe.rowWindow = true;
  return importOriginal();
});

vi.mock('@/features/sources/SourceEventsTable', async (importOriginal) => {
  probe.table = true;
  return importOriginal();
});

vi.mock('@/features/sources/SourceEventMapModal', async (importOriginal) => {
  probe.modal = true;
  return importOriginal();
});

import { SourcesPage } from '@/pages/admin/SourcesPage';

/**
 * ⛔ Прогрів `@mantine/dates` — не прискорення, а причина зависання гейта `client` (2026-10-01: воркер vitest
 * мовчав 36 хв на цьому файлі й помер з кодом 1, `testTimeout` не спрацював).
 *
 * Фоновий `CollectionRunsPanel` тягне `DateInput` через `lazy(import('@/shared/dates/DateInputWithStyles'))`.
 * Холодний `import()` цього модуля довший за підняття шухляди, тож на момент `fireEvent.click(tab)` його межа
 * `<Suspense>` ще чекає — і клік (синхронний `act`) застає ДВІ незавершені лінивості: цю й саму вкладку. React
 * усередині синхронного скидання черги `act` рендерить дерево знову й знову (зміряно в налагоджувачі: жодного
 * `scheduleUpdateOnFiber`, лише `handleThrow` з тим самим `Promise` по черзі в `FilterBar` і в `TabsPanel`), а
 * мікрозадачі, що розв'язали б `import()`, не отримують ходу ніколи. Цикл синхронний, тому таймер межі тесту теж
 * не отримує ходу. Залежало від того, чи встиг `import()` до кліку: локально (холодно) — зависання щоразу, у CI —
 * зрідка.
 *
 * ⚠ Прогрівається лише спільний модуль дат, а НЕ модулі під перевіркою (`SourceEventsTable`,
 * `SourceEventMapModal`, `RowWindowMapsPanel`): їхні позначки `probe` лишаються чесними.
 */
beforeAll(async () => {
  await import('@/shared/dates/DateInputWithStyles');
}, 60_000);

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const Connection = {
  catalog: null,
  code: 'PI-MAIN',
  collectionSchedules: 0,
  endpoint: 'Server=pi-main',
  hasSecret: false,
  id: 7,
  isActive: true,
  maxParallel: 4,
  nameL10n: { en: 'Main PI server' },
  rowVersion: 'AAAAAAAAB9E=',
  secondaryEndpoint: null,
  sourceEntities: 1,
  transport: 'PiSqlClient',
};

function respond(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input), 'http://localhost').pathname;

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
      if (path === '/api/v1/data-sources') return json([Connection]);
      if (path === '/api/v1/sources') {
        return json([
          {
            code: 'FLARE_EVENTS',
            dataSourceCode: 'PI-MAIN',
            dataSourceId: 7,
            displayName: null,
            entityPath: null,
            id: 42,
            isActive: true,
            lastRun: null,
            oldestGap: null,
            onMissingInSource: 'MarkOrphaned',
            registryDefId: null,
            transport: 'PiSqlClient',
            validFromAttribute: null,
            validToAttribute: null,
            validToInclusive: false,
          },
        ]);
      }
      if (path === '/api/v1/data-sources/7/event-templates') return json([]);
      if (path === '/api/v1/source-event-maps') return json([]);
      if (path === '/api/v1/documents') return json({ items: [], nextCursor: null, totalCount: 0 });
      if (path === '/api/v1/sources/42/source-events') return json({ items: [], nextCursor: null, totalCount: 0 });

      return json([]);
    }),
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('«Події з PI» — ліниво', () => {
  it('вкладка довантажується лише на клік, форма мапінгу — лише на «Створити мапінг»', async () => {
    respond();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <MemoryRouter initialEntries={['/admin/sources?panel=PI-MAIN']}>
            <SourcesPage />
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>,
    );

    const drawer = await screen.findByRole('dialog');
    const tab = await within(drawer).findByRole('tab', { name: '⟦sourceEvents.tab⟧' });
    expect(probe.table).toBe(false);
    expect(probe.modal).toBe(false);
    expect(probe.rowWindow).toBe(false);

    fireEvent.click(tab);

    await waitFor(() => expect(drawer.querySelector('[data-source-events-tab="PI-MAIN"]')).not.toBeNull());
    expect(probe.table).toBe(true);
    expect(probe.modal).toBe(false);
    await waitFor(() => expect(drawer.querySelector('[data-row-window-maps]')).not.toBeNull());
    expect(probe.rowWindow).toBe(true);

    fireEvent.click(await within(drawer).findByRole('button', { name: '⟦sourceEvents.mapCreate⟧' }));

    expect(await screen.findByRole('dialog', { name: '⟦sourceEvents.mapCreateTitle⟧' })).toBeTruthy();
    expect(probe.modal).toBe(true);
  });
});
