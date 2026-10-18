import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { RegistryDefDto } from '@/api/types';
import { loadCatalog } from '@/shared/i18n';
import { RegistriesPage } from '@/pages/admin/RegistriesPage';
import { testTheme } from '@/test/render';

/**
 * Рядок дій сторінки записів довідника (KIT.md): одна головна дія, не більше двох другорядних,
 * решта — в меню «More». До цього в шапці стояло дев'ять кнопок поспіль.
 *
 * Мутаційні докази: повернути «Export» у `secondary` — перший тест червоніє (три видимі кнопки
 * збирають усі в меню); прибрати `onClick` пункту «Switch master source» — п'ятий.
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
  'registries.constructor': 'Registry designer',
  'registries.impact.open': 'Affected documents',
  'registries.data.open': 'Open data',
  'registries.export.button': 'Export',
  'registries.export.csv': 'Export CSV',
  'registries.export.xlsx': 'Export XLSX',
  'registries.sourceSwitch': 'Switch master source',
  'registries.sourceSwitchHint': 'Hint',
  'registries.sourceSwitchWarning': 'Warning',
  'registries.moreActions': 'More',
  'registries.list.back': 'All registries',
  'registry.import.pick': 'Import CSV',
  'common.delete': 'Delete',
  'common.cancel': 'Cancel',
  'common.save': 'Save',
};

const Registry: RegistryDefDto = {
  id: 1,
  code: 'UNITS',
  nameL10n: { values: { en: 'Units' } },
  fields: [],
  isTemporal: false,
  isHierarchical: false,
  sourceKind: 'Local',
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function serve(permissions: string[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });
      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions,
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }
      if (url.includes('/api/v1/languages')) return json([{ code: 'en', isDefault: true, nameNative: 'English' }]);
      if (url.includes('/entries')) return json([]);
      if (url.includes('/api/v1/registries')) return json([Registry]);

      return json(null);
    }),
  );
}

async function show(permissions: string[]): Promise<void> {
  serve(permissions);
  await loadCatalog('en', 'private');

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={['/admin/registries?code=UNITS']}>
          <RegistriesPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );

  // Профіль і довідник мають ПРИЇХАТИ: до них кнопок немає, і «немає кнопок» було б зеленим на будь-якому коді.
  await screen.findByRole('button', { name: 'New entry' });
}

const AllRights = ['Registry.View', 'Registry.EditData', 'Registry.EditDefinition', 'Integration.Manage'];

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('RegistriesPage: рядок дій', () => {
  it('на видноті одна головна дія і дві другорядні; решта — лише в меню', async () => {
    await show(AllRights);

    expect(screen.getByRole('button', { name: 'New entry' }).getAttribute('data-variant')).toBe('filled');
    expect(screen.getByRole('link', { name: 'Open data' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Import CSV' })).toBeDefined();

    for (const hidden of ['Export', 'Registry designer', 'Affected documents', 'New registry', 'Switch master source']) {
      expect(screen.queryByRole('button', { name: hidden })).toBeNull();
      expect(screen.queryByRole('link', { name: hidden })).toBeNull();
    }
  });

  it('меню «More» містить решту дій; без прав — лише те, що доступне', async () => {
    await show(AllRights);

    fireEvent.click(screen.getByRole('button', { name: 'More' }));
    for (const item of ['Export', 'Registry designer', 'Affected documents', 'New registry', 'Switch master source']) {
      expect(await screen.findByRole('menuitem', { name: item })).toBeDefined();
    }
  });

  it('лише перегляд: немає ні «New entry», ні «Import CSV», ні «New registry» та «Switch master source»', async () => {
    serve(['Registry.View']);
    await loadCatalog('en', 'private');
    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <MemoryRouter initialEntries={['/admin/registries?code=UNITS']}>
            <RegistriesPage />
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>,
    );

    await screen.findByRole('link', { name: 'Open data' });
    expect(screen.queryByRole('button', { name: 'New entry' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Import CSV' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'More' }));
    expect(await screen.findByRole('menuitem', { name: 'Export' })).toBeDefined();
    expect(screen.queryByRole('menuitem', { name: 'New registry' })).toBeNull();
    expect(screen.queryByRole('menuitem', { name: 'Switch master source' })).toBeNull();
  });

  it('«Export» з меню відкриває діалог експорту (CSV/XLSX), а не випадне вікно кнопки', async () => {
    await show(AllRights);

    fireEvent.click(screen.getByRole('button', { name: 'More' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Export' }));

    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('Export CSV');
    expect(dialog.textContent).toContain('Export XLSX');
  });

  it('«Switch master source» з меню відкриває діалог перемикання', async () => {
    await show(AllRights);

    fireEvent.click(screen.getByRole('button', { name: 'More' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Switch master source' }));

    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('Warning');
  });

  it('«Import CSV» відкриває вибір файлу', async () => {
    await show(AllRights);
    const click = vi.spyOn(HTMLInputElement.prototype, 'click');

    fireEvent.click(screen.getByRole('button', { name: 'Import CSV' }));

    expect(click).toHaveBeenCalledTimes(1);
    expect((click.mock.contexts[0] as HTMLInputElement).type).toBe('file');
  });
});
