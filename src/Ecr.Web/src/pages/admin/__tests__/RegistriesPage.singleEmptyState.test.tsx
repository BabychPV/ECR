import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { loadCatalog } from '@/shared/i18n';
import { RegistriesPage } from '../RegistriesPage';
import { testTheme } from '@/test/render';

/**
 * `U-08` — правило порожніх станів: **на екрані одночасно видно рівно ОДИН
 * порожній стан — той, що пояснює найближчу перешкоду**.
 *
 * ⛔ На чистій базі сторінка показувала ДВА порожні стани один під одним:
 * «No registries yet» (межа переліку довідників) і «Pick a registry above…»
 * (таблиця записів). Друга порада нездійсненна САМЕ тоді, коли показана
 * перша: обирати нема з чого.
 *
 * ⛔ Мутаційний доказ — прибрати `hasRegistries &&` перед `<DataTable>` у
 * `RegistriesPage.tsx`: перший випадок стає ЧЕРВОНИМ (два заголовки рівня 4
 * замість одного). Другий випадок стереже протилежний бік — що правка не
 * з'їла таблицю разом із її станом там, де довідники Є: підміна умови на
 * `false` робить червоним саме його.
 */

const SeededStrings: Record<string, string> = {
  'registries.title': 'Registries',
  'registries.empty': 'No registries yet',
  'registries.emptyHint':
    'Registries hold the reference lists columns pick from: equipment, substances, units.',
  'registries.pick': 'Pick a registry',
  'registries.pickHint': 'Pick a registry above to see its entries and validity windows.',
  'registries.noEntries': 'This registry has no entries',
  'registries.noEntriesHint':
    'Columns that look this registry up will offer nothing to choose from.',
  'registries.code': 'Code',
  'registries.name': 'Name',
  'registries.parent': 'Parent',
  'registries.validity': 'Valid',
  'registries.search': 'Search',
  'registries.searchPlaceholder': 'Filter by code or name',
  'registries.searchNoMatches': 'No entries match this search.',
  'filters.clear': 'Clear',
};

const me = {
  denies: [],
  grants: {},
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: [],
  simulatedForUserId: null,
  userId: 1,
  userName: 'tester',
};

const registry = {
  id: 1,
  code: 'UNITS',
  nameL10n: { values: { en: 'Units' } },
  fields: [],
  isTemporal: false,
  isHierarchical: false,
  sourceKind: 'Master',
};

function mockFetch(registries: readonly unknown[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      }
      if (url.includes('/api/v1/me')) return json(me);
      if (url.includes('/entries')) return json([]);
      if (url.includes('/api/v1/registries')) return json(registries);

      return json(null);
    }),
  );
}

function show(path: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[path]}>
          <RegistriesPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/**
 * Заголовки порожніх станів на екрані.
 *
 * ⚠ Рівень 4 навмисно: і `EmptyState`, і `ForbiddenState` малюють
 * `<Title order={4}>`, а заголовок сторінки — інший рівень. Тобто цей локатор
 * рахує САМЕ «екранів-заглушок», не будь-який текст.
 */
function emptyStateHeadings(): string[] {
  return screen.queryAllByRole('heading', { level: 4 }).map((node) => node.textContent ?? '');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RegistriesPage: рівно один порожній стан (U-08)', () => {
  it('довідників немає — видно лише «No registries yet», без «Pick a registry»', async () => {
    mockFetch([]);
    await loadCatalog('en', 'private');

    show('/admin/registries');

    await screen.findByText('No registries yet');

    /*
     * ⛔ Ядро випадку. Без `hasRegistries` тут ДВА заголовки: «No registries
     * yet» і «Pick a registry» — і другий радить дію, яку виконати нічим.
     */
    expect(
      emptyStateHeadings(),
      'на екрані більше одного порожнього стану',
    ).toEqual(['No registries yet']);

    /*
     * ⚠ Окремо й поіменно: `queryAllByRole` міг би зрівнятися випадково, якби
     * «Pick a registry» колись переїхав на інший рівень заголовка. Тут
     * перевіряється, що цього ТЕКСТУ на екрані немає взагалі.
     *
     * ⚠ Плейсхолдер `Select` із тим самим рядком локатором не ловиться — він
     * атрибут, а не текстовий вузол; підказка «Pick a registry above…» —
     * ловиться, і її теж бути не повинно.
     */
    expect(screen.queryByText('Pick a registry')).toBeNull();
    expect(
      screen.queryByText('Pick a registry above to see its entries and validity windows.'),
    ).toBeNull();
  });

  it('довідники є, але жоден не обрано — таблиця на місці й каже «оберіть довідник»', async () => {
    mockFetch([registry]);
    await loadCatalog('en', 'private');

    show('/admin/registries');

    /*
     * ⛔ Зворотний бік правила: перешкоду «довідників немає» знято, тож
     * підпорядкований розділ ЗНОВУ має право говорити — і «Pick a registry»
     * тут єдиний порожній стан, бо межа переліку мовчить.
     */
    await screen.findByText('Pick a registry above to see its entries and validity windows.');

    expect(emptyStateHeadings()).toEqual(['Pick a registry']);
    expect(screen.queryByText('No registries yet')).toBeNull();
  });
});
