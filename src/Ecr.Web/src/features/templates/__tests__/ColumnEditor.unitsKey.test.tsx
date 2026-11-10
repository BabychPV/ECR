import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RegistryDefinitionDto } from '@/api/types';
import { queryKeys } from '@/api/queryKeys';
import { ColumnEditor } from '@/features/templates/ColumnEditor';
import { emptyColumnDraft, type ColumnDraft } from '@/features/templates/column';
import { RegistryConstructorPage } from '@/pages/admin/RegistryConstructorPage';
import { testTheme } from '@/test/render';

/**
 * D13: каталог одиниць редактор колонки шаблону і конструктор довідника читають ОДНИМ
 * ключем `queryKeys.units.all()`. Літерал `['units']` у кожному файлі окремо мовчки
 * розходився б від першої ж правки одного з них: інвалідація каталогу (`UnitsPage` після
 * збереження одиниці) не дійшла б до другого екрана, і він показував би стару одиницю.
 *
 * ⚠ Обидва компоненти змонтовані в ОДНОМУ `QueryClient`: тоді рівно один запис кешу з двома
 * спостерігачами і рівно один запит — доказ спільного ключа поведінкою, а не текстом файлу.
 */

const Definition: RegistryDefinitionDto = {
  id: 4,
  code: 'LIMITS',
  nameL10n: { values: { en: 'Limits' } },
  isTemporal: true,
  sourceKind: 'Local',
  definitionVersion: 3,
  dataRevision: 11,
  fields: [
    {
      id: 41,
      code: 'Limit',
      nameL10n: { values: { en: 'Limit value' } },
      dataType: 'Decimal',
      isRequired: true,
      isScopeField: false,
      lookupRegistryDefId: null,
      unitId: 3,
    },
  ],
  relations: [],
  rules: [],
  mappings: [],
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function mockServer(): { unitCalls: () => number } {
  let unitCalls = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: {} });

      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Registry.View', 'Registry.EditDefinition'],
          simulatedForUserId: null,
          userId: 9,
          userName: 'tester',
        });
      }

      if (path.endsWith('/api/v1/units')) {
        unitCalls += 1;
        return json([{ id: 3, code: 't', dimensionCode: 'MASS' }]);
      }

      if (path.endsWith('/definition')) return json(Definition);
      if (path.endsWith('/history')) return json([]);
      if (path.endsWith('/api/v1/users')) return json({ items: [], totalCount: 0 });
      if (path.endsWith('/api/v1/registries')) return json([]);
      if (/\/styles$/.test(path)) return json([]);

      return json(null);
    }),
  );

  return { unitCalls: () => unitCalls };
}

const decimalDraft: ColumnDraft = {
  ...emptyColumnDraft(1),
  code: 'Amount',
  headerL10n: { en: 'Amount' },
  dataType: 'Decimal',
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('D13: ключ каталогу одиниць спільний для ColumnEditor і RegistryConstructorPage', () => {
  it('фабрика зберігає форму `[\'units\']`, за якою інвалідують решта екранів', () => {
    expect(queryKeys.units.all()).toEqual(['units']);
  });

  it('один запис кешу, два спостерігачі, один запит; інвалідація каталогу перечитує обидва одним запитом', async () => {
    const server = mockServer();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <MemoryRouter initialEntries={['/admin/registries/LIMITS/definition']}>
            <ColumnEditor
              draft={decimalDraft}
              disabled={false}
              saving={false}
              templateVersionId={1}
              onChange={() => {}}
              onSubmit={() => {}}
              onCancel={() => {}}
            />
            <Routes>
              <Route path="/admin/registries/:code/definition" element={<RegistryConstructorPage />} />
            </Routes>
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>,
    );

    // ⚠ Спершу дочекатися, що конструктор справді має числове поле з одиницею (тобто його
    // запит одиниць увімкнений), — інакше «один спостерігач» був би зеленим і без спільного ключа.
    await screen.findByText('Limit value');

    await waitFor(() => {
      const unitQueries = client.getQueryCache().findAll({ queryKey: ['units'] });
      expect(unitQueries).toHaveLength(1);
      expect(unitQueries[0]?.queryKey).toEqual(queryKeys.units.all());
      expect(unitQueries[0]?.getObserversCount()).toBe(2);
      expect(unitQueries[0]?.state.status).toBe('success');
    });
    expect(server.unitCalls()).toBe(1);

    await client.invalidateQueries({ queryKey: queryKeys.units.all() });

    await waitFor(() => {
      expect(server.unitCalls()).toBe(2);
    });
    expect(client.getQueryCache().findAll({ queryKey: ['units'] })).toHaveLength(1);
  });
});
