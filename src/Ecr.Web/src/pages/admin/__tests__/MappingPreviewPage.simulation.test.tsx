import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { MappingPreviewPage } from '@/pages/admin/MappingPreviewPage';
import { MeQueryKey } from '@/shared/session/useSession';
import { testTheme } from '@/test/render';

/**
 * L9-18: під симуляцією «очима користувача» сервер відхиляє КОЖЕН не-GET (`ECR-SIM-0403`), а `can()` бачить
 * права ЦІЛІ — «Add mapping» і кнопки паузи/відновлення мапінгу не пропонуються (так само, як `SheetActions`).
 *
 * ⛔ Мутаційний доказ: поверни `mayManage = can(...)` без `isSimulation` — кнопки з'являються, тест червоніє.
 */
const Preview = {
  sourceEntityId: 1,
  code: 'FLARE_01',
  displayName: 'Flare 01',
  fromUtc: '2026-09-01T00:00:00Z',
  toUtc: '2026-09-08T00:00:00Z',
  pointsSeen: 3,
  isTruncated: false,
  fields: [
    {
      fieldMapId: 1,
      sourceField: 'Flare_01_CO',
      outcome: 'Materialized',
      targetRowKey: 'Flare_01',
      targetColumnDefId: 100,
      targetColumnCode: 'CO_MASS',
      aggregation: 'Sum',
      sourceUnitCode: 'kg',
      targetUnitCode: 't',
      pointCount: 2,
      foldedValue: '42.5',
      isActive: true,
      pendingSourceUnitChange: null,
    },
  ],
  rows: [],
  unmappedSourceFields: [],
  uncoveredColumns: [],
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function show(isSimulation: boolean): QueryClient {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: {} });
      if (url.includes('/api/v1/me')) {
        return json({
          denies: [], grants: {}, isSimulation, language: 'en', mustChangePassword: false,
          permissions: ['Integration.Manage'], simulatedForUserId: null, userId: 1, userName: 'tester',
        });
      }
      if (url.includes('/mapping/preview')) return json(Preview);
      if (url.includes('/api/v1/sources')) {
        return json([{ id: 1, code: 'FLARE_01', displayName: 'Flare 01', dataSourceId: 3 }]);
      }

      return json(null);
    }),
  );

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/mapping?entity=1']}>
          <MappingPreviewPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );

  return client;
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('MappingPreviewPage: симуляція (L9-18)', () => {
  it('під симуляцією немає «Add mapping» і паузи мапінгу', async () => {
    const client = show(true);

    await screen.findByText('Flare_01_CO');
    // ⚠ Профіль має ПРИЇХАТИ: до нього `can()` дає `false`, і «немає кнопок» було б зеленим на будь-якому коді.
    await waitFor(() => expect(client.getQueryData(MeQueryKey)).toBeDefined());
    await act(async () => {
      await Promise.resolve();
    });

    expect(screen.queryByText('⟦mapping.create⟧')).toBeNull();
    expect(screen.queryByText('⟦mapping.pause⟧')).toBeNull();
  });

  it('контроль: без симуляції кнопки є', async () => {
    show(false);

    await screen.findByText('Flare_01_CO');

    expect(await screen.findByText('⟦mapping.create⟧')).toBeDefined();
    expect(await screen.findByText('⟦mapping.pause⟧')).toBeDefined();
  });
});
