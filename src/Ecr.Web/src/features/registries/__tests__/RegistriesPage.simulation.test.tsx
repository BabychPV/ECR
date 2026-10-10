import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { RegistryDefDto } from '@/api/types';
import { loadCatalog } from '@/shared/i18n';
import { RegistriesPage } from '@/pages/admin/RegistriesPage';
import { MeQueryKey } from '@/shared/session/useSession';
import { testTheme } from '@/test/render';

/**
 * L9-18: під симуляцією «очима користувача» (`isSimulation`) сервер відхиляє КОЖЕН не-GET (`ECR-SIM-0403`), а
 * `can()` бачить права ЦІЛІ. Перелік довідників не пропонує створення й правки, які напевно впадуть `403`.
 *
 * ⛔ Мутаційний доказ: прибери `&& !simulation` з `canEditData`/`canEditDefinition` у `RegistriesPage.tsx` —
 * «New entry», «New registry» і колонка «Edit» з'являються під симуляцією, перший тест червоніє.
 */
const Strings: Record<string, string> = {
  'registries.title': 'Registries',
  'registries.pick': 'Pick a registry',
  'registries.code': 'Code',
  'registries.name': 'Name',
  'registries.parent': 'Parent',
  'registries.validity': 'Valid',
  'registries.editEntry': 'Edit',
  'registries.newEntry': 'New entry',
  'registries.newRegistry': 'New registry',
  'registries.search': 'Search',
  'registries.searchPlaceholder': 'Filter by code or name',
  'registries.constructor': 'Constructor',
  'common.delete': 'Delete',
  'common.cancel': 'Cancel',
  'common.save': 'Save',
};

const registry: RegistryDefDto = {
  id: 1,
  code: 'UNITS',
  nameL10n: { values: { en: 'Units' } },
  fields: [],
  isTemporal: false,
  isHierarchical: false,
  sourceKind: 'Local',
};

const entry = { id: 42, code: 'KG', display: 'Kilogram', parentEntryId: null, validFrom: null, validTo: null };

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function serve(isSimulation: boolean): void {
  const me = {
    denies: [],
    grants: {},
    isSimulation,
    language: 'en',
    mustChangePassword: false,
    permissions: ['Registry.View', 'Registry.EditData', 'Registry.EditDefinition'],
    simulatedForUserId: null,
    userId: 1,
    userName: 'tester',
  };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });
      if (url.includes('/api/v1/me')) return json(me);
      if (url.includes('/api/v1/languages')) return json([{ code: 'en', isDefault: true, nameNative: 'English' }]);
      if (url.includes('/entries')) return json([entry]);
      if (url.includes('/api/v1/registries')) return json([registry]);

      return json(null);
    }),
  );
}

async function show(isSimulation: boolean): Promise<void> {
  serve(isSimulation);
  await loadCatalog('en', 'private');

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/registries?code=UNITS']}>
          <RegistriesPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );

  await screen.findByText('Kilogram');
  // ⚠ Профіль має ПРИЇХАТИ: до нього `can()` дає `false`, і «немає кнопок» було б зеленим на будь-якому коді.
  await waitFor(() => expect(client.getQueryData(MeQueryKey)).toBeDefined());
  await act(async () => {
    await Promise.resolve();
  });
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('RegistriesPage: симуляція (L9-18)', () => {
  it('під симуляцією немає створення довідника, запису й правки', async () => {
    await show(true);

    expect(screen.queryByRole('button', { name: 'New registry' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'New entry' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Edit' })).toBeNull();
    // Читання лишається: перехід у конструктор і дані — GET.
    expect(screen.getByRole('link', { name: 'Constructor' })).toBeDefined();
  });

  it('контроль: без симуляції ті самі кнопки є', async () => {
    await show(false);

    expect(screen.getByRole('button', { name: 'New registry' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'New entry' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Edit' })).toBeDefined();
  });
});
