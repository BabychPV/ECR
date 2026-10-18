import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RegistryDefinitionDto } from '@/api/types';
import { RegistryConstructorPage } from '@/pages/admin/RegistryConstructorPage';
import { testTheme } from '@/test/render';

/**
 * Вкладки конструктора довідника: що показується, що ховається і що пояснено.
 *
 * ⚠ Каталог рядків не завантажений — підписи приходять ключами в `⟦…⟧`.
 *
 * Мутаційні докази: прибрати `showMapping` — перший тест червоніє; прибрати `back` — другий;
 * прибрати `entryCount` зі сторінки — тест «Обов'язкове» червоніє.
 */

const Base: RegistryDefinitionDto = {
  id: 4,
  code: 'PERMIT',
  nameL10n: { values: { en: 'Permits' } },
  isTemporal: false,
  sourceKind: 'Local',
  definitionVersion: 3,
  dataRevision: 11,
  fields: [
    {
      id: 41,
      code: 'Number',
      nameL10n: { values: { en: 'Permit number' } },
      dataType: 'String',
      isRequired: true,
      isScopeField: true,
      lookupRegistryDefId: null,
      unitId: null,
    },
  ],
  relations: [],
  rules: [],
  mappings: [],
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function mockServer(definition: RegistryDefinitionDto, entryCount: number): void {
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

      if (path.endsWith('/api/v1/users')) return json({ items: [], totalCount: 0 });
      if (path.endsWith('/definition')) return json(definition);
      if (path.endsWith('/definition/draft')) return json({ draft: null });
      if (path.endsWith('/history')) return json([]);
      if (path.endsWith('/usage')) return json({ total: 0, items: [] });

      if (path.endsWith('/api/v1/registries')) {
        return json([
          {
            id: 4,
            code: 'PERMIT',
            nameL10n: { values: { en: 'Permits' } },
            fields: [],
            isHierarchical: false,
            isTemporal: false,
            sourceKind: 'Local',
            entryCount,
          },
        ]);
      }

      return json(null);
    }),
  );
}

function show(): void {
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
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RegistryConstructorPage: вкладки', () => {
  it('локальний довідник без мапінгу не має вкладки «Мапінг»', async () => {
    mockServer(Base, 0);
    show();

    await screen.findByRole('tab', { name: /registries\.tabHistory/ });
    expect(screen.queryByRole('tab', { name: /registries\.tabMapping/ })).toBeNull();
  });

  it('мапінг є, або довідник зовнішній — вкладка лишається з поясненням «лише читання»', async () => {
    mockServer({ ...Base, sourceKind: 'External' }, 0);
    show();

    fireEvent.click(await screen.findByRole('tab', { name: /registries\.tabMapping/ }));
    expect(await screen.findByText(/registries\.tabHint\.mapping/)).toBeDefined();
    expect(screen.getByText(/registries\.noMappings/)).toBeDefined();
  });

  it('є кнопка «Назад» до записів довідника й рядок пояснення сторінки', async () => {
    mockServer(Base, 0);
    show();

    const back = await screen.findByRole('link', { name: /registries\.data\.back/ });
    expect(back.getAttribute('href')).toBe('/admin/registries?code=PERMIT');
    expect(screen.getByText(/registries\.constructor\.description/)).toBeDefined();
  });

  it('історія й «де використано» мають коротке пояснення, порожні зв\'язки — що робити', async () => {
    mockServer(Base, 0);
    show();

    fireEvent.click(await screen.findByRole('tab', { name: /registries\.tabRelations/ }));
    expect(await screen.findByText(/registries\.tabHint\.relationsEmpty/)).toBeDefined();

    fireEvent.click(screen.getByRole('tab', { name: /registries\.tabHistory/ }));
    expect(await screen.findByText(/registries\.tabHint\.history/)).toBeDefined();

    fireEvent.click(screen.getByRole('tab', { name: /registries\.tabUsage/ }));
    expect(await screen.findByText(/registries\.tabHint\.usage/)).toBeDefined();

    fireEvent.click(screen.getByRole('tab', { name: /registries\.tabRules/ }));
    expect(await screen.findByText(/registries\.tabHint\.rulesExample/)).toBeDefined();
  });
});

describe('RegistryConstructorPage: обов\'язкове нове поле', () => {
  async function openNewField(): Promise<HTMLInputElement> {
    fireEvent.click(await screen.findByRole('button', { name: /registries\.addField/ }));
    return (await screen.findByRole('checkbox', { name: /registries\.required/ })) as HTMLInputElement;
  }

  it('у довіднику без записів прапорець активний', async () => {
    mockServer(Base, 0);
    show();

    const box = await openNewField();
    // ⚠ Перелік довідників міг ще їхати: до його приходу прапорець вимкнений, потім — активний.
    await vi.waitFor(() => {
      expect(box.disabled).toBe(false);
    });
  });
});
