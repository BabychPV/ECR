import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, useLocation } from 'react-router-dom';
import type { JSX } from 'react';
import { loadCatalog } from '@/shared/i18n';
import { RegistriesPage } from '@/pages/admin/RegistriesPage';
import { testTheme } from '@/test/render';
import { PageDescriptionContext } from '@/shared/ui/pageDescription';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { describe as report, findViolations } from '@/test/a11y';

/**
 * `UI-35`: `/admin/registries` без `?code=` — перелік довідників зі смугою
 * показників і шторкою (макет `screens-data.js` `/admin/registries`).
 *
 * ⛔ Мутаційні докази (локально, не в коміті):
 *  - `code === null ? <RegistriesList />` → `<RegistryOverview />` у
 *    `RegistriesPage.tsx`: червоні всі випадки, крім «з ?code= — сторінка довідника»;
 *  - `matchesStat` завжди `true`: червоний «показник фільтрує»;
 *  - `canSeeUsage={true}`: червоний «без права — розділу Used in немає»;
 *  - прибрати `closeDrawer`: червоний «фокус повертається на назву».
 */

const me = (permissions: readonly string[]) => ({
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

const field = (id: number, code: string) => ({
  id,
  code,
  dataType: 'String',
  isRequired: false,
  isScopeField: false,
  lookupRegistryDefId: null,
  nameL10n: { values: { en: code } },
});

const Registries = [
  {
    id: 1,
    code: 'UNITS',
    nameL10n: { values: { en: 'Units of measure' } },
    fields: [field(1, 'Symbol'), field(2, 'Dimension')],
    isTemporal: false,
    isHierarchical: false,
    sourceKind: 'Local',
  },
  {
    id: 2,
    code: 'SOURCES',
    nameL10n: { values: { en: 'Emission sources' } },
    fields: [field(3, 'Facility')],
    isTemporal: true,
    isHierarchical: true,
    sourceKind: 'External',
  },
];

const Usage = {
  total: 3,
  items: [
    { id: 'c-1', kind: 'TemplateColumn', label: 'Template 1 · column 2', name: null, route: null },
  ],
};

function mockFetch(permissions: readonly string[], registries: readonly unknown[] = Registries) {
  const calls: string[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      calls.push(url);

      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Catalog });
      if (url.includes('/api/v1/me')) return json(me(permissions));
      if (url.includes('/usage')) return json(Usage);
      if (url.includes('/entries')) return json([]);
      if (url.includes('/api/v1/registries')) return json(registries);

      return json(null);
    }),
  );

  return calls;
}

function Where(): JSX.Element {
  const location = useLocation();

  return <output data-testid="where">{`${location.pathname}${location.search}`}</output>;
}

function show(path: string): HTMLElement {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[path]}>
          <RegistriesPage />
          <Where />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  ).container;
}

const where = (): string => screen.getByTestId('where').textContent ?? '';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RegistriesList (UI-35)', () => {
  it('перелік: назва з кодом, поля, ознаки; колонок без даних немає', async () => {
    mockFetch(['Registry.View']);
    await loadCatalog('en', 'private');
    show('/admin/registries');

    await screen.findByRole('button', { name: 'Units of measure' });
    expect(screen.getByText('SOURCES')).toBeTruthy();

    const headers = screen.getAllByRole('columnheader').map((cell) => cell.textContent?.trim());
    expect(headers).toEqual(['Registry', 'Fields', 'Properties']);

    // ⛔ D15-06: у `RegistryDefDto` немає ні лічильника записів, ні «де використано».
    expect(screen.queryByRole('columnheader', { name: /Entries|Used in|Updated|State/ })).toBeNull();

    // ⚠ Без права заведення — головної дії немає (а не неактивна).
    expect(screen.queryByRole('button', { name: 'New registry' })).toBeNull();
    // ⛔ Select «Pick a registry» більше не вхід у довідник.
    expect(screen.queryByRole('textbox', { name: 'Registries' })).toBeNull();
  });

  it('смуга показників: усього, із вікном чинності, із PI AF; показник фільтрує перелік', async () => {
    mockFetch(['Registry.View']);
    await loadCatalog('en', 'private');
    show('/admin/registries');

    const strip = await screen.findByRole('group', { name: 'Registry summary' });
    expect(within(strip).getByText('registries')).toBeTruthy();

    fireEvent.click(within(strip).getByRole('button', { name: /synced from PI AF/ }));

    await waitFor(() => expect(where()).toContain('stat=external'));
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Units of measure' })).toBeNull());
    expect(screen.getByRole('button', { name: 'Emission sources' })).toBeTruthy();
  });

  it('пошук за назвою чи кодом; без збігів — «нічого не знайдено» з кнопкою скидання', async () => {
    mockFetch(['Registry.View']);
    await loadCatalog('en', 'private');
    show('/admin/registries');

    await screen.findByRole('button', { name: 'Units of measure' });
    fireEvent.change(screen.getByLabelText('Search'), { target: { value: 'zzz' } });

    await screen.findByText('No registries match these filters.');
    // ⚠ «Clear» два: у рядку фільтрів і в стані «нічого не знайдено»; стан — без `data-filter-clear`.
    const clear = screen
      .getAllByRole('button', { name: 'Clear' })
      .find((button) => !button.hasAttribute('data-filter-clear'));
    // Поле пошуку приймає скидання, коли не у фокусі (`useFieldDraft`).
    screen.getByLabelText('Search').blur();
    fireEvent.click(clear!);
    await waitFor(() => expect(where()).not.toContain('q='));
    await screen.findByRole('button', { name: 'Units of measure' });
  });

  it('клац по назві відкриває шторку за ?panel=<code>; «Open data» веде в /entries', async () => {
    mockFetch(['Registry.View', 'Registry.EditDefinition']);
    await loadCatalog('en', 'private');
    show('/admin/registries');

    fireEvent.click(await screen.findByRole('button', { name: 'Emission sources' }));

    await waitFor(() => expect(where()).toContain('panel=SOURCES'));
    const drawer = await screen.findByRole('dialog');

    expect(within(drawer).getByText('Synced from PI AF', { selector: 'dd *, dd' })).toBeTruthy();
    expect(within(drawer).getByText('time-bound · hierarchical')).toBeTruthy();
    await within(drawer).findByText('3 references');

    expect(within(drawer).getByRole('link', { name: 'Open data' }).getAttribute('href')).toBe(
      '/admin/registries/SOURCES/entries',
    );
    expect(within(drawer).getByRole('link', { name: 'Registry designer' }).getAttribute('href')).toBe(
      '/admin/registries/SOURCES/definition',
    );
    expect(within(drawer).getByRole('link', { name: 'Entries and validity' }).getAttribute('href')).toBe(
      '/admin/registries?code=SOURCES',
    );
  });

  it('без Registry.EditDefinition — розділу «Used in» немає і запиту /usage теж', async () => {
    const calls = mockFetch(['Registry.View']);
    await loadCatalog('en', 'private');
    show('/admin/registries?panel=UNITS');

    const drawer = await screen.findByRole('dialog');
    expect(within(drawer).getByText('Kept in ECR')).toBeTruthy();
    expect(within(drawer).queryByText('Used in')).toBeNull();
    expect(calls.some((url) => url.includes('/usage'))).toBe(false);
  });

  it('закриття шторки повертає фокус на назву в рядку', async () => {
    mockFetch(['Registry.View']);
    await loadCatalog('en', 'private');
    show('/admin/registries');

    const opener = await screen.findByRole('button', { name: 'Units of measure' });
    opener.focus();
    fireEvent.click(opener);

    const drawer = await screen.findByRole('dialog');
    fireEvent.click(within(drawer).getByRole('button', { name: 'Close' }));

    await waitFor(() => expect(where()).not.toContain('panel='));
    await waitFor(() =>
      expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Units of measure' })),
    );
  });

  it('з ?code= — сторінка довідника з посиланням назад до переліку', async () => {
    mockFetch(['Registry.View']);
    await loadCatalog('en', 'private');
    show('/admin/registries?code=UNITS');

    const back = await screen.findByRole('link', { name: '← All registries' });
    expect(back.getAttribute('href')).toBe('/admin/registries');
    expect(screen.queryByRole('group', { name: 'Registry summary' })).toBeNull();
  });

  it('axe: перелік і відкрита шторка без порушень', async () => {
    mockFetch(['Registry.View', 'Registry.EditDefinition']);
    await loadCatalog('en', 'private');
    const container = show('/admin/registries?panel=SOURCES');

    await screen.findByRole('button', { name: 'Units of measure' });
    await screen.findByText('3 references');

    const violations = await findViolations(container.ownerDocument.body);
    expect(violations, report(violations)).toEqual([]);
  });

  describe('агрегати переліку (lane ui-registry-def-ext)', () => {
    const month = new Date().toISOString().slice(0, 7);

    const Extended = [
      {
        ...Registries[0],
        entryCount: 12,
        definitionVersion: 2,
        dataChangedAt: `${month}-02T10:00:00Z`,
        usedInColumns: 0,
        usedInTemplates: 0,
        hasDraft: false,
      },
      {
        ...Registries[1],
        entryCount: 30,
        definitionVersion: 3,
        dataChangedAt: '2020-01-05T10:00:00Z',
        usedInColumns: 7,
        usedInTemplates: 2,
        hasDraft: true,
      },
    ];

    it('колонки Entries / Used in / Entries changed / State і смуга макета', async () => {
      mockFetch(['Registry.View', 'Registry.EditDefinition'], Extended);
      await loadCatalog('en', 'private');
      show('/admin/registries');

      await screen.findByRole('button', { name: 'Units of measure' });
      const headers = screen.getAllByRole('columnheader').map((cell) => cell.textContent?.trim());
      expect(headers).toEqual(['Registry', 'Entries', 'Fields', 'Used in', 'Entries changed', 'State', 'Properties']);

      const strip = screen.getByRole('group', { name: 'Registry summary' });
      expect(strip.textContent).toContain('42');
      expect(strip.textContent).toContain('entries');

      // «changed this month» фільтрує: лишається лише UNITS.
      fireEvent.click(within(strip).getByRole('button', { name: /changed this month/ }));
      await waitFor(() => expect(screen.queryByRole('button', { name: 'Emission sources' })).toBeNull());
      expect(screen.getByRole('button', { name: 'Units of measure' })).toBeTruthy();
    });

    it('без права: usedIn/hasDraft = null — колонок Used in і State немає', async () => {
      mockFetch(
        ['Registry.View'],
        Extended.map((row) => ({ ...row, usedInColumns: null, usedInTemplates: null, hasDraft: null })),
      );
      await loadCatalog('en', 'private');
      show('/admin/registries');

      await screen.findByRole('button', { name: 'Units of measure' });
      const headers = screen.getAllByRole('columnheader').map((cell) => cell.textContent?.trim());
      expect(headers).toEqual(['Registry', 'Entries', 'Fields', 'Entries changed', 'Properties']);
    });

    it('шторка: записи, використання, версія з чернеткою і банер', async () => {
      mockFetch(['Registry.View', 'Registry.EditDefinition'], Extended);
      await loadCatalog('en', 'private');
      show('/admin/registries?panel=SOURCES');

      const drawer = await screen.findByRole('dialog');
      expect(within(drawer).getByText('30')).toBeTruthy();
      expect(within(drawer).getByText('columns: 7 · templates: 2')).toBeTruthy();
      expect(within(drawer).getByText('v3 published · draft in progress')).toBeTruthy();
      expect(within(drawer).getByText('The definition has unpublished changes')).toBeTruthy();
    });
  });
});

describe('RegistriesList: одне пояснення під заголовком (звірка batch-4 з макетом, п.17)', () => {
  it('власне пояснення переліку заміняє пояснення маршруту, а не стоїть другим рядком', async () => {
    mockFetch(['Registry.View']);
    await loadCatalog('en', 'private');
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <MemoryRouter initialEntries={['/admin/registries']}>
            <PageDescriptionContext.Provider value="nav.registries.description">
              <RegistriesPage />
            </PageDescriptionContext.Provider>
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>,
    );

    await screen.findByRole('button', { name: 'Units of measure' });
    const lines = screen.getAllByTestId('page-description');
    expect(lines).toHaveLength(1);
    expect(lines[0]?.textContent).toMatch(/^Reference lists that cells/);
    // ⚠ Пояснення маршруту в каталозі — той самий текст, тож дубль ловиться лише числом входжень.
    expect(screen.getAllByText(/^Reference lists that cells/)).toHaveLength(1);
  });
});
