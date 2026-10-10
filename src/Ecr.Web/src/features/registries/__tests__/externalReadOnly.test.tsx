import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { RegistryDefDto } from '@/api/types';
import { loadCatalog, t } from '@/shared/i18n';
import { RegistriesPage } from '@/pages/admin/RegistriesPage';
import { RegistryEntryEditor } from '@/features/registries/RegistryEntryEditor';
import { MeQueryKey } from '@/shared/session/useSession';
import { testTheme } from '@/test/render';

/**
 * D-211: записи довідника з `sourceKind = External` вручну не змінюються —
 * master AF, сервер відмовляє `409 ECR-REG-0409` (`externalSource`). Інтерфейс
 * не пропонує дії, яка напевно впаде: створення, вікно чинності, видалення й
 * імпорт неактивні, форма запису — лише перегляд, і підказка каже чому.
 *
 * ⛔ Мутація: прибрати `disabled={externalReadOnly}` з будь-якої кнопки в
 * `RegistriesPage.tsx` або `readOnly` у `RegistryEntryEditor.tsx` — червоний
 * відповідний `expect`; контроль із `Local` тримає, що неактивність — саме
 * через джерело, а не через право чи стан сторінки.
 */

const SeededStrings: Record<string, string> = {
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
  'registries.externalReadOnly': 'Entries of this registry are synchronized from AF and cannot be edited here.',
  'registry.import.pick': 'Import CSV',
  'common.delete': 'Delete',
  'common.cancel': 'Cancel',
  'common.save': 'Save',
};

const me = {
  denies: [],
  grants: {},
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: ['Registry.EditData'],
  simulatedForUserId: null,
  userId: 1,
  userName: 'tester',
};

function registryOf(sourceKind: RegistryDefDto['sourceKind']): RegistryDefDto {
  return {
    id: 1,
    code: 'UNITS',
    nameL10n: { values: { en: 'Units' } },
    fields: [
      {
        id: 7,
        code: 'QTY',
        nameL10n: { values: { en: 'Quantity' } },
        dataType: 'Decimal',
        isRequired: false,
        isScopeField: false,
        lookupRegistryDefId: null,
        unitId: null,
      },
    ],
    isTemporal: true,
    isHierarchical: false,
    sourceKind,
  };
}

const entry = { id: 42, code: 'KG', display: 'Kilogram', parentEntryId: null, validFrom: null, validTo: null };

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function serve(registry: RegistryDefDto, meOverride: Partial<typeof me> = {}): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      if (url.includes('/api/v1/me')) return json({ ...me, ...meOverride });
      if (url.includes('/api/v1/languages')) return json([{ code: 'en', isDefault: true, nameNative: 'English' }]);
      if (url.includes('/entries/42')) {
        return json({ id: 42, code: 'KG', displayL10n: { values: { en: 'Kilogram' } }, values: { QTY: '1.5' } });
      }
      if (url.includes('/entries')) return json([entry]);
      if (url.includes('/api/v1/registries')) return json([registry]);

      return json(null);
    }),
  );
}

function showPage(): QueryClient {
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

  return client;
}

function showEditor(registry: RegistryDefDto): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <RegistryEntryEditor registry={registry} entry={entry} opened onClose={() => undefined} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function isDisabled(element: HTMLElement): boolean {
  return (element as HTMLButtonElement).disabled;
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('D-211: довідник External — записи лише для перегляду', () => {
  it('сторінка: створення, вікно, видалення й імпорт неактивні, підказка видима', async () => {
    serve(registryOf('External'));
    await loadCatalog('en', 'private');

    showPage();
    const row = (await screen.findByText('Kilogram')).closest('tr')!;

    expect(isDisabled(screen.getByRole('button', { name: 'New entry' }))).toBe(true);
    expect(isDisabled(screen.getByRole('button', { name: 'Import CSV' }))).toBe(true);
    expect(isDisabled(within(row).getByRole('button', { name: 'Valid' }))).toBe(true);
    expect(isDisabled(within(row).getByRole('button', { name: 'Delete' }))).toBe(true);

    // Перегляд запису лишається доступним — форма відкриється лише для читання.
    expect(isDisabled(within(row).getByRole('button', { name: 'Edit' }))).toBe(false);
    expect(screen.getByText(SeededStrings['registries.externalReadOnly']!)).toBeDefined();
  });

  it('контроль: Local — ті самі кнопки активні, підказки немає', async () => {
    serve(registryOf('Local'));
    await loadCatalog('en', 'private');

    showPage();
    const row = (await screen.findByText('Kilogram')).closest('tr')!;

    expect(isDisabled(screen.getByRole('button', { name: 'New entry' }))).toBe(false);
    expect(isDisabled(screen.getByRole('button', { name: 'Import CSV' }))).toBe(false);
    expect(isDisabled(within(row).getByRole('button', { name: 'Valid' }))).toBe(false);
    expect(isDisabled(within(row).getByRole('button', { name: 'Delete' }))).toBe(false);
    expect(screen.queryByText(SeededStrings['registries.externalReadOnly']!)).toBeNull();
  });

  it('форма запису External: поля й збереження неактивні, підказка видима', async () => {
    serve(registryOf('External'));
    await loadCatalog('en', 'private');

    showEditor(registryOf('External'));

    const qty = await screen.findByDisplayValue('1.5', {}, { timeout: 10_000 });
    expect((qty as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByLabelText(t('registries.code')) as HTMLInputElement).disabled).toBe(true);
    expect(isDisabled(screen.getByRole('button', { name: t('common.save') }))).toBe(true);
    expect(screen.getByText(SeededStrings['registries.externalReadOnly']!)).toBeDefined();
  });
});

/**
 * L9-18: під симуляцією «очима користувача» (`isSimulation`) сервер відхиляє КОЖЕН не-GET (`ECR-SIM-0403`), а
 * `can()` бачить права ЦІЛІ. Перелік довідників не пропонує створення й правки, які напевно впадуть `403`.
 *
 * ⛔ Мутаційний доказ: прибери `&& !simulation` з `canEditData`/`canEditDefinition` у `RegistriesPage.tsx` —
 * «New entry», «New registry» і «Edit» з'являються під симуляцією, перший тест червоніє.
 */
describe('L9-18: симуляція — довідники лише для читання', () => {
  const AllRights = ['Registry.View', 'Registry.EditData', 'Registry.EditDefinition'];

  async function showSimulated(isSimulation: boolean): Promise<void> {
    serve(registryOf('Local'), { isSimulation, permissions: AllRights });
    await loadCatalog('en', 'private');

    const client = showPage();
    await screen.findByText('Kilogram');
    // ⚠ Профіль має ПРИЇХАТИ: до нього `can()` дає `false`, і «немає кнопок» було б зеленим на будь-якому коді.
    await waitFor(() => expect(client.getQueryData(MeQueryKey)).toBeDefined());
    await act(async () => {
      await Promise.resolve();
    });
  }

  it('під симуляцією немає створення довідника, запису й правки; перехід у конструктор лишається', async () => {
    await showSimulated(true);

    expect(screen.queryByRole('button', { name: 'New registry' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'New entry' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Edit' })).toBeNull();
    expect(screen.getByRole('link', { name: 'Constructor' })).toBeDefined();
  });

  it('контроль: без симуляції ті самі кнопки є', async () => {
    await showSimulated(false);

    expect(await screen.findByRole('button', { name: 'New registry' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'New entry' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Edit' })).toBeDefined();
  });
});
