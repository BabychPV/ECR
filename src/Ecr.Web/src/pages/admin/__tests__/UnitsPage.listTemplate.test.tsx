import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, useLocation } from 'react-router-dom';
import type { JSX } from 'react';
import { loadCatalog } from '@/shared/i18n';
import { UnitsPage, baseUnits } from '../UnitsPage';
import { testTheme } from '@/test/render';

/**
 * UI-21: довідник одиниць на шаблоні переліку (макет `screens-data.js`
 * `/admin/units`, `26-units.png`).
 *
 * Що стережеться: пояснення під заголовком; смуга показників, де «зі
 * зсувом» фільтрує; пошук і розмірність у рядку фільтрів; колонка «Base
 * unit»; правка й видалення — у шторці за `?panel=`, а не в рядку.
 */

const SeededStrings: Record<string, string> = {
  'units.title': 'Units of measure',
  'units.description': 'Units of measure grouped by dimension.',
  'units.code': 'Unit',
  'units.dimension': 'Dimension',
  'units.factor': 'Factor to base',
  'units.offset': 'Offset to base',
  'units.base': 'base',
  'units.baseUnit': 'Base unit',
  'units.conversion': 'Conversion',
  'units.whereUsed': 'Where used',
  'units.deleteUnused': 'Nothing refers to this unit.',
  'units.symbol': 'Symbol',
  'units.name': 'Name',
  'units.edit': 'Edit',
  'units.convert': 'Convert',
  'units.new': 'New unit',
  'units.checkConversion': 'Check a conversion',
  'units.statsLabel': 'Units at a glance',
  'units.statUnits': 'units',
  'units.statDimensions': 'dimensions',
  'units.statOffset': 'with an offset (temperature)',
  'units.search': 'Search',
  'units.searchPlaceholder': 'Code or dimension',
  'units.noMatch': 'No units match the filters.',
  'units.empty': 'No units registered',
  'filters.clear': 'Clear',
  'common.delete': 'Remove',
  'common.close': 'Close',
};

const units = [
  { id: 1, code: 'kg', dimensionId: 1, factorToBase: '1.0000000000', offsetToBase: '0.0000000000', dimensionCode: 'Mass' },
  { id: 2, code: 't', dimensionId: 1, factorToBase: '1000.0000000000', offsetToBase: '0.0000000000', dimensionCode: 'Mass' },
  { id: 3, code: 'K', dimensionId: 2, factorToBase: '1.0000000000', offsetToBase: '0.0000000000', dimensionCode: 'Temperature' },
  { id: 4, code: 'degC', dimensionId: 2, factorToBase: '1.0000000000', offsetToBase: '273.1500000000', dimensionCode: 'Temperature' },
];

function mockApi(permissions: string[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: SeededStrings });

      if (url.endsWith('/api/v1/me')) {
        return json({
          userId: 1,
          userName: 'bootstrap',
          language: 'en',
          permissions,
          isSimulation: false,
          denies: [],
          grants: {},
          mustChangePassword: false,
          simulatedForUserId: null,
        });
      }

      if (url.endsWith('/api/v1/units/2/usage')) return json({ total: 0, items: [] });

      if (url.endsWith('/api/v1/units/2') && method === 'GET') {
        return json({
          ...units[1],
          isBase: false,
          rowVersion: 'AAAAAAAAB9E=',
          symbolL10n: { en: 't' },
          nameL10n: { en: 'Tonne' },
        });
      }

      if (url.endsWith('/api/v1/units') && method === 'GET') return json(units);

      throw new Error(`Немає мока для ${method} ${url}`);
    }),
  );
}

let location = '';

function LocationProbe(): JSX.Element | null {
  location = useLocation().search;
  return null;
}

async function show(permissions: string[], entry = '/admin/units'): Promise<HTMLElement> {
  mockApi(permissions);
  await loadCatalog('en', 'private');

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[entry]}>
          <UnitsPage />
          <LocationProbe />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );

  const table = await screen.findByRole('table');
  await within(table).findByRole('button', { name: 't' });
  return table;
}

function shownCodes(table: HTMLElement): string[] {
  return Array.from(table.querySelectorAll('[data-unit-open]')).map((node) => node.getAttribute('data-unit-open') ?? '');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('baseUnits: базова одиниця розмірності — за значенням, не за текстом', () => {
  it('множник 1 і зсув 0; зсув ≠ 0 базовою не робить', () => {
    const bases = baseUnits(units);

    expect(bases.get(1)).toBe('kg');

    // ⛔ Мутаційний доказ: приберіть умову про зсув — базовою стане `degC`
    // (перша за кодом із множником 1), і «1 K» рахувалося б від °C.
    expect(bases.get(2)).toBe('K');
  });
});

describe('UnitsPage на шаблоні переліку (UI-21)', () => {
  it('пояснення під заголовком і смуга: одиниці, розмірності, зі зсувом', async () => {
    await show([]);

    // Рівно одне пояснення під заголовком — у рядку пояснення, не другим `meta` (batch-2-a, дефект 3).
    expect(screen.getAllByTestId('page-description').map((node) => node.textContent)).toEqual(['Units of measure grouped by dimension.']);

    const strip = screen.getByRole('group', { name: 'Units at a glance' });
    expect(within(strip).getByText('units').closest('[data-stat]')?.textContent).toContain('4');
    expect(within(strip).getByText('dimensions').closest('[data-stat]')?.textContent).toContain('2');
    expect(within(strip).getByText('with an offset (temperature)').closest('[data-stat]')?.textContent).toContain('1');
  });

  it('показник «зі зсувом» фільтрує перелік і знімається «Clear»', async () => {
    const table = await show([]);

    fireEvent.click(screen.getByRole('button', { name: /with an offset/ }));

    await waitFor(() => {
      expect(shownCodes(table)).toEqual(['degC']);
    });
    expect(location).toContain('stat=offset');
  });

  it('пошук і розмірність — у рядку фільтрів, значення в адресі', async () => {
    const table = await show([], '/admin/units?dimension=Mass');

    await waitFor(() => {
      expect(shownCodes(table).sort()).toEqual(['kg', 't']);
    });

    fireEvent.change(screen.getByLabelText('Search'), { target: { value: 'temp' } });

    // ⚠ Мас + «temp» — нічого: порожнеча саме «нічого не підійшло», а не
    // «довідник порожній».
    expect(await screen.findByText('No units match the filters.')).toBeDefined();
    expect(screen.queryByText('No units registered')).toBeNull();
  });

  it('колонка «Base unit»: базова позначена, решта називає свою базову', async () => {
    const table = await show([]);

    const tonne = table.querySelector('[data-unit-open="t"]')?.closest('tr') as HTMLElement;
    const kilo = table.querySelector('[data-unit-open="kg"]')?.closest('tr') as HTMLElement;

    expect(within(tonne).getAllByText('kg')).toHaveLength(1);
    expect(within(kilo).getByText('base')).toBeDefined();

    // Множник без хвостових нулів, зсув 0 — порожня клітинка, не «0».
    expect(within(tonne).getByText('1,000')).toBeDefined();
    expect(within(tonne).queryByText('0')).toBeNull();
  });

  it('у рядку немає «Edit»/«Remove»; вони у шторці, що відкривається за ?panel=', async () => {
    const table = await show(['Uom.EditCatalog']);

    expect(within(table).queryByRole('button', { name: /Edit|Remove/ })).toBeNull();

    fireEvent.click(within(table).getByRole('button', { name: 't' }));

    const drawer = await screen.findByRole('dialog', { name: /t\s*Mass/ });
    expect(location).toContain('panel=unit-2');

    // Деталі — з лінивого чанка.
    expect(await within(drawer).findByText('1 t = 1,000 kg')).toBeDefined();
    expect(await within(drawer).findByText('Tonne')).toBeDefined();
    expect(await within(drawer).findByText('Nothing refers to this unit.')).toBeDefined();

    expect(within(drawer).getByRole('button', { name: 'Edit' })).toBeDefined();
    expect(within(drawer).getByRole('button', { name: 'Remove' })).toBeDefined();
  });

  it('головна дія «New unit» — лише з правом; «Check a conversion» — завжди', async () => {
    await show([]);

    expect(await screen.findByRole('button', { name: 'Check a conversion' })).toBeDefined();
    expect(screen.queryByRole('button', { name: 'New unit' })).toBeNull();
  });
});
