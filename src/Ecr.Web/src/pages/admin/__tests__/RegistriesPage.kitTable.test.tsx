import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { loadCatalog } from '@/shared/i18n';
import { RegistriesPage } from '../RegistriesPage';
import { testTheme } from '@/test/render';

/**
 * Переїзд переліку записів довідника на таблицю набору (`KIT.md` §6.5,
 * директива №15 §3).
 *
 * ⛔ Тест написаний ДО переїзду і на незміненому файлі ЧЕРВОНИЙ — інакше він
 * не доводив би нічого. Сира `<Table>` не вміє двох речей, які тут
 * перевіряються:
 *   1. шапка не є кнопкою і не несе `aria-sort` — тобто для того, хто не
 *      бачить екрана, порядок колонки не існує як поняття;
 *   2. у стані «фільтр нічого не знайшов» немає чим цей фільтр скинути —
 *      єдина дія, яка з цього стану виводить, лишалася на здогад.
 *
 * ⛔ Третє твердження — НЕ про нову поведінку, а про збережену: «довідник
 * порожній» і «фільтр нічого не знайшов» — два різні екрани, і вони були
 * різними ДО переїзду (`AsyncBoundary` проти власного `<Text>`). Саме це
 * найлегше втратити при переїзді: `DataTable` бачить порожній масив в обох
 * випадках і відрізнити їх може лише викликач (`filtered`). Тому обидва
 * випадки перевіряються окремо, і кожен називає, чого на екрані бути НЕ
 * повинно.
 *
 * ⚠ Права `Registry.EditData` профіль НЕ має навмисно: без нього в рядках
 * немає кнопок дій, і «Code» на екрані рівно одне — шапка колонки. Інакше
 * локатор кнопки шапки конкурував би з кнопками рядків.
 */

const SeededStrings: Record<string, string> = {
  'registries.title': 'Registries',
  'registries.pick': 'Pick a registry',
  'registries.pickHint': 'Pick a registry above to see its entries and validity windows.',
  'registries.code': 'Code',
  'registries.name': 'Name',
  'registries.parent': 'Parent',
  'registries.validity': 'Valid',
  'registries.search': 'Search',
  'registries.searchPlaceholder': 'Filter by code or name',
  'registries.searchNoMatches': 'No entries match this search.',
  'registries.noEntries': 'This registry has no entries',
  'registries.noEntriesHint':
    'Columns that look this registry up will offer nothing to choose from.',
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

/**
 * Порядок із сервера навмисно НЕ збігається з жодним із двох порядків
 * сортування: інакше «клац переставив рядки» було б неможливо відрізнити від
 * «нічого не сталося».
 */
const Entries = [
  { id: 2, code: 'B_TONNE', display: 'Tonne', parentEntryId: null, validFrom: null, validTo: null },
  {
    id: 1,
    code: 'A_KILOGRAM',
    display: 'Kilogram',
    parentEntryId: null,
    validFrom: null,
    validTo: null,
  },
  { id: 3, code: 'C_GRAM', display: 'Gram', parentEntryId: null, validFrom: null, validTo: null },
];

function mockFetch(entries: readonly unknown[]): void {
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
      if (url.includes('/entries')) return json(entries);
      if (url.includes('/api/v1/registries')) return json([registry]);

      return json(null);
    }),
  );
}

function show(): void {
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
}

/** Значення ПЕРШОЇ колонки всіх рядків — у тому порядку, як вони в DOM. */
function codeColumn(): string[] {
  return Array.from(document.querySelectorAll('tbody tr')).map(
    (row) => row.querySelector('td')?.textContent ?? '',
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RegistriesPage: перелік записів на таблиці набору', () => {
  it('шапка «Code» — кнопка з aria-sort, і клац по ній переставляє рядки', async () => {
    mockFetch(Entries);
    await loadCatalog('en', 'private');

    show();

    await screen.findByText('Kilogram');

    // Порядок при відкритті — той, що прийшов із сервера: таблиця нічого не
    // сортує, доки її про це не попросили.
    expect(codeColumn()).toEqual(['B_TONNE', 'A_KILOGRAM', 'C_GRAM']);

    const header = screen.getByRole('columnheader', { name: 'Code' });

    /*
     * ⛔ ЧЕРВОНЕ на сирій `<Table>`: `Table.Th` без `aria-sort` віддає `null`.
     * Це і є те, чого набір додає — стан сортування, оголошений стандартним
     * атрибутом, а не намальованою стрілкою.
     */
    expect(
      header.getAttribute('aria-sort'),
      'шапка колонки не оголошує стану сортування (aria-sort)',
    ).toBe('none');

    const button = within(header).getByRole('button', { name: 'Code' });

    fireEvent.click(button);

    expect(header.getAttribute('aria-sort')).toBe('ascending');
    expect(codeColumn()).toEqual(['A_KILOGRAM', 'B_TONNE', 'C_GRAM']);

    fireEvent.click(button);

    expect(header.getAttribute('aria-sort')).toBe('descending');
    expect(codeColumn()).toEqual(['C_GRAM', 'B_TONNE', 'A_KILOGRAM']);
  });

  it('ідентифікатор батька показано як ідентифікатор, а не як число з розрядами', async () => {
    /*
     * ⛔ Пастка самого набору, ширша за проп `num`: `Cell` жене БУДЬ-ЯКЕ числове
     * поле через `formatNumber`, тож ідентифікатор `1234` виїхав би на екран як
     * `1,234` — число, якого в базі немає й за яким нічого не знайти. Колонка
     * закривається власним `render`, і саме його стереже цей випадок.
     *
     * ⚠ Чотиризначний батько навмисно: на `7` роздільник не з'являється, і
     * твердження було б зеленим із фіксом і без нього.
     */
    mockFetch([
      { id: 9, code: 'CHILD', display: 'Child', parentEntryId: 1234, validFrom: null, validTo: null },
    ]);
    await loadCatalog('en', 'private');

    show();

    await screen.findByText('Child');

    expect(screen.getByText('1234')).toBeDefined();
    expect(screen.queryByText('1,234')).toBeNull();
  });

  it('довідник без записів — «записів немає», і НЕ «фільтр нічого не знайшов»', async () => {
    mockFetch([]);
    await loadCatalog('en', 'private');

    show();

    expect(await screen.findByText('This registry has no entries')).toBeDefined();

    /*
     * ⛔ Друга половина твердження важливіша за першу: сказати «нічого не
     * знайдено за фільтром» там, де довідник просто порожній, означає
     * відправити людину чистити фільтр, якого вона не ставила.
     */
    expect(screen.queryByText('No entries match this search.')).toBeNull();
  });

  it('фільтр без збігів — «нічого не знайдено», і НЕ «записів немає»', async () => {
    mockFetch(Entries);
    await loadCatalog('en', 'private');

    show();

    await screen.findByText('Kilogram');

    fireEvent.change(screen.getByLabelText('Search'), {
      target: { value: 'no-such-entry-anywhere' },
    });

    expect(await screen.findByText('No entries match this search.')).toBeDefined();

    /*
     * ⛔ Дзеркальна половина попереднього випадку: записи в довіднику Є, і
     * «цей довідник порожній» тут — неправда. Обидва `queryBy` разом і
     * тримають різницю, яку переїзд найлегше втрачає.
     */
    expect(screen.queryByText('This registry has no entries')).toBeNull();
    expect(screen.queryByText('Kilogram')).toBeNull();
  });

  it('зі стану «нічого не знайдено» фільтр скидається названою кнопкою', async () => {
    mockFetch(Entries);
    await loadCatalog('en', 'private');

    show();

    await screen.findByText('Kilogram');

    fireEvent.change(screen.getByLabelText('Search'), {
      target: { value: 'no-such-entry-anywhere' },
    });

    expect(screen.queryByText('Kilogram')).toBeNull();

    /*
     * ⛔ ЧЕРВОНЕ на сирій `<Table>`: власний перемикач станів писав пояснення
     * текстом і не давав із цього стану ЖОДНОЇ дії.
     *
     * ⚠ Локатор — за `data-table-clear-filters`, а не за іменем: рядок
     * фільтрів має власну кнопку скидання з тим самим підписом, і пошук за
     * роллю знайшов би дві. Ім'я перевіряється окремо — саме воно доводить,
     * що підпис прийшов із каталогу (`filters.clear`), а не лишився гліфом
     * `×` із дефолту набору.
     *
     * ⚠ Цей випадок навмисно НЕ чекає на заголовок «нічого не знайдено»:
     * інакше мутація в `noMatchTitle` валила б і його теж, і два твердження
     * перестали б бути незалежними.
     */
    const clear = document.querySelector<HTMLButtonElement>('[data-table-clear-filters="true"]');

    expect(clear, 'у стані «фільтр нічого не знайшов» немає чим скинути фільтр').not.toBeNull();
    expect(clear?.textContent).toBe('Clear');

    fireEvent.click(clear as HTMLButtonElement);

    expect(await screen.findByText('Kilogram')).toBeDefined();
  });
});
