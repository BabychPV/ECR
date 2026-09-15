import type { JSX } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { loadCatalog } from '@/shared/i18n';
import { RegistriesPage } from '../RegistriesPage';

/**
 * UI-аудит-пас 8, lane4, два незалежні пункти на одній сторінці:
 *
 * 1. (п.5) Довге ім'я запису довідника без пробілів (~570 символів у «Name ·
 *    English») не переноситься і не обтинається — розтягує клітинку, таблицю
 *    й ВСЮ сторінку.
 * 2. (п.6) Таблиця записів довідника — голий список без пошуку чи фільтра.
 *
 * ⛔ `Select` (`@mantine/core`) — довідник-пікер у шапці — зависає під jsdom
 * (`Q-299`), тому заглушений легким `<select>` тим самим прийомом, що й
 * `RegistriesPage.newRegistrySilentFailure.test.tsx` поруч.
 */
vi.mock('@mantine/core', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/core')>();

  type StubOption = { value: string; label: string };
  type StubSelectProps = {
    data?: (string | StubOption)[];
    value?: string | null;
    onChange?: (value: string | null) => void;
    label?: string;
    placeholder?: string;
    'aria-label'?: string;
  };

  function StubSelect(props: StubSelectProps): JSX.Element {
    const options = (props.data ?? []).map((item) =>
      typeof item === 'string' ? { value: item, label: item } : item,
    );

    return (
      <select
        aria-label={props['aria-label'] ?? props.label ?? props.placeholder}
        value={props.value ?? ''}
        onChange={(event) => props.onChange?.(event.target.value === '' ? null : event.target.value)}
      >
        <option value="" />
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
    );
  }

  return { ...actual, Select: StubSelect };
});

const SeededStrings: Record<string, string> = {
  'registries.title': 'Registries',
  'registries.pick': 'Pick a registry',
  'registries.code': 'Code',
  'registries.name': 'Name',
  'registries.parent': 'Parent',
  'registries.validity': 'Valid',
  'registries.search': 'Search',
  'registries.searchPlaceholder': 'Filter by code or name',
  'registries.searchNoMatches': 'No entries match this search.',
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

// ⛔ П.5: ~570 символів без жодного пробілу — той самий розмір, що описаний
// в аудиті ("~570 символів").
const longName = 'x'.repeat(570);

const entries = [
  { id: 1, code: 'SHORT', display: 'Kilogram', parentEntryId: null, validFrom: null, validTo: null },
  { id: 2, code: 'LONG', display: longName, parentEntryId: null, validFrom: null, validTo: null },
];

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) {
        return new Response(
          JSON.stringify({ languageCode: 'en', revision: 1, strings: SeededStrings }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      if (url.includes('/api/v1/me')) {
        return new Response(JSON.stringify(me), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      if (url.includes('/entries')) {
        return new Response(JSON.stringify(entries), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      if (url.includes('/api/v1/registries')) {
        return new Response(JSON.stringify([registry]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(null), { status: 200 });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/registries?code=UNITS']}>
          <RegistriesPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RegistriesPage: записи довідника (аудит-пас 8, lane4, п.5 і п.6)', () => {
  it('п.5: довге ім\'я без пробілів доходить до DOM ЦІЛИМ і має клас переносу', async () => {
    mockFetch();
    await loadCatalog('en', 'private');

    show();

    const cell = await screen.findByText(longName);

    // ⛔ Мутаційний доказ: значення НЕ обрізане в коді (повне значення в
    // DOM — обрізання, якщо колись знадобиться, мало б лишатися на боці
    // CSS/браузера, а не ховати дані) і клітинка несе клас, що дозволяє
    // браузеру переносити рядок замість розтягування таблиці. Видалення
    // `className="ecr-wrap-anywhere"` у `RegistriesPage.tsx` зробить другий
    // `expect` червоним.
    expect(cell.textContent).toBe(longName);
    expect(cell.className).toContain('ecr-wrap-anywhere');
  });

  it('п.6: пошук фільтрує рядки за кодом чи назвою', async () => {
    mockFetch();
    await loadCatalog('en', 'private');

    show();

    await screen.findByText('Kilogram');
    expect(screen.getByText(longName)).toBeDefined();

    const search = screen.getByLabelText('Search');
    fireEvent.change(search, { target: { value: 'short' } });

    // ⛔ Головне твердження: рядок «LONG» зникає, «SHORT» лишається —
    // фільтр застосований, а не косметичний інпут без ефекту.
    expect(screen.getByText('Kilogram')).toBeDefined();
    expect(screen.queryByText(longName)).toBeNull();
  });

  it('п.6: пошук без збігів показує пояснення, а не порожню таблицю', async () => {
    mockFetch();
    await loadCatalog('en', 'private');

    show();

    await screen.findByText('Kilogram');

    fireEvent.change(screen.getByLabelText('Search'), {
      target: { value: 'no-such-entry-anywhere' },
    });

    expect(await screen.findByText('No entries match this search.')).toBeDefined();
    expect(screen.queryByText('Kilogram')).toBeNull();
  });
});
