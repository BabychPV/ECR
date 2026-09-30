import { afterEach, describe, expect, it, vi } from 'vitest';
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
const probe = vi.hoisted(() => ({ table: false, modal: false }));

vi.mock('@/features/sources/SourceEventsTable', async (importOriginal) => {
  probe.table = true;
  return importOriginal();
});

vi.mock('@/features/sources/SourceEventMapModal', async (importOriginal) => {
  probe.modal = true;
  return importOriginal();
});

import { SourcesPage } from '@/pages/admin/SourcesPage';

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

    fireEvent.click(tab);

    await waitFor(() => expect(drawer.querySelector('[data-source-events-tab="PI-MAIN"]')).not.toBeNull());
    expect(probe.table).toBe(true);
    expect(probe.modal).toBe(false);

    fireEvent.click(await within(drawer).findByRole('button', { name: '⟦sourceEvents.mapCreate⟧' }));

    expect(await screen.findByRole('dialog', { name: '⟦sourceEvents.mapCreateTitle⟧' })).toBeTruthy();
    expect(probe.modal).toBe(true);
  });
});
