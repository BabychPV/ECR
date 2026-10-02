import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RegistryDefinitionDto } from '@/api/types';
import { RegistryConstructorPage } from '@/pages/admin/RegistryConstructorPage';
import { testTheme } from '@/test/render';

/**
 * Вкладка «Зв'язки» конструктора й право `Registry.EditDefinition` (`ФВ-8.12`, порція 1).
 *
 * ⛔ Це перевірка на РІВНІ СТОРІНКИ: компонент `RegistryRelations` сам лише слухняно слідує
 * `canEdit`, а що саме йому передають, вирішує сторінка. Без права на опис вибір цілі зв'язку
 * (`select`) бути не повинен — сервер однаково відмовив би `403`.
 *
 * Мутаційний доказ: `canEdit={true}` на `RegistryRelations` у сторінці робить червоним тест
 * «без права — лише читання».
 */
const Definition: RegistryDefinitionDto = {
  id: 4,
  code: 'PERMIT',
  nameL10n: { values: { en: 'Permits' } },
  isTemporal: false,
  sourceKind: 'Local',
  definitionVersion: 3,
  dataRevision: 1,
  fields: [
    {
      id: 41, code: 'Number', nameL10n: { values: { en: 'Permit number' } }, dataType: 'String', isRequired: true,
      isScopeField: true, lookupRegistryDefId: null, unitId: null,
    },
    {
      id: 42, code: 'Substance', nameL10n: { values: { en: 'Substance link' } }, dataType: 'Lookup', isRequired: false,
      isScopeField: false, lookupRegistryDefId: 5, unitId: null,
    },
  ],
  relations: [
    {
      kind: 'Cascade', fieldCode: 'Substance', targetRegistryDefId: 5, targetRegistryCode: 'SUBSTANCE',
      linkKind: null, linkCount: null,
    },
  ],
  rules: [],
  mappings: [],
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function mockServer(permissions: string[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: {} });
      }

      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false,
          permissions, simulatedForUserId: null, userId: 9, userName: 'tester',
        });
      }

      if (path.endsWith('/definition')) {
        return json(Definition);
      }

      if (path.endsWith('/api/v1/registries')) {
        return json([
          { id: 5, code: 'SUBSTANCE', nameL10n: { values: { en: 'Substances' } }, fields: [], isHierarchical: false, isTemporal: false, sourceKind: 'Master' },
          { id: 6, code: 'SITE', nameL10n: { values: { en: 'Sites' } }, fields: [], isHierarchical: false, isTemporal: false, sourceKind: 'Master' },
        ]);
      }

      if (path.endsWith('/definition/draft')) {
        return json({ definitionVersion: 3, draft: null });
      }

      return json(null);
    }),
  );
}

async function openRelations(): Promise<void> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/registries/PERMIT/definition']}>
          <Routes>
            <Route path="/admin/registries/:code/definition" element={<RegistryConstructorPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );

  // ⚠ Каталог рядків тут порожній — вкладка шукається за ключем, а не за перекладом.
  await screen.findByText('Permit number');
  fireEvent.click(await screen.findByRole('tab', { name: /registries\.tabRelations/ }));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RegistryConstructorPage: вкладка «Зв\'язки» й право на опис', () => {
  it('з правом Registry.EditDefinition — вибір цілі зв\'язку є', async () => {
    mockServer(['Registry.View', 'Registry.EditDefinition']);
    await openRelations();

    await waitFor(() => {
      expect(screen.getByRole('combobox')).toBeDefined();
    });
  });

  it('без права — лише читання: цілі зв\'язку видно, вибору немає', async () => {
    mockServer(['Registry.View']);
    await openRelations();

    // ⚠ Спершу дочекатися самої цілі: порожній екран зробив би «немає select» зеленим на будь-якому коді.
    await screen.findByText('SUBSTANCE');

    expect(screen.queryByRole('combobox')).toBeNull();
  });
});
