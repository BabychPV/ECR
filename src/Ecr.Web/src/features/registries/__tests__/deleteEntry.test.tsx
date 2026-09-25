import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { loadCatalog } from '@/shared/i18n';
import { RegistriesPage } from '@/pages/admin/RegistriesPage';
import { testTheme } from '@/test/render';

/**
 * Споживач `DELETE /api/v1/registries/{code}/entries/{id}` (директива №15,
 * `BE-01`).
 *
 * ⛔ Обробник видалення жив на сервері без жодної кнопки: перевіряв право,
 * рахував посилання, піднімав ревізію — і не викликався ніколи.
 *
 * ⚠ Другий тест — головний. Відмова `ECR-REG-0409` не є аварією: на запис
 * посилаються дані, і єдина дія, яка має сенс далі, — закрити запис датою.
 * «Повторити» тут дасть ту саму відмову назавжди.
 */

const SeededStrings: Record<string, string> = {
  'registries.title': 'Registries',
  'registries.pick': 'Pick a registry',
  'registries.code': 'Code',
  'registries.name': 'Name',
  'registries.parent': 'Parent',
  'registries.validity': 'Valid',
  'registries.validFrom': 'Valid from',
  'registries.validTo': 'Valid to',
  'registries.validityHint': 'Closing by date keeps history readable.',
  'registries.editEntry': 'Edit',
  'registries.search': 'Search',
  'registries.searchPlaceholder': 'Filter by code or name',
  'registries.referencedBy': 'Referenced by',
  'registries.referenceKind.cells': 'Document cells',
  'registries.referenceKind.methodologyConstants': 'Methodology constants',
  'common.delete': 'Delete',
  'registries.deleteEntryTitle': 'Delete entry "{code}"?',
  'common.cancel': 'Cancel',
  'common.save': 'Save',
};

const me = {
  denies: [],
  grants: {},
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,

  // ⚠ Саме `Registry.EditData`: без нього кнопки немає взагалі, і тест
  // доводив би лише те, що сторінка рендериться.
  permissions: ['Registry.EditData'],
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

const entry = {
  id: 42,
  code: 'KG',
  display: 'Kilogram',
  parentEntryId: null,
  validFrom: null,
  validTo: null,
};

/** Відмова сервера «на запис посилаються дані» у форматі `problem+json`. */
const inUse = {
  title: 'Registry entry is in use',
  status: 409,
  detail: 'Entry «KG» is referenced by 7 cells.',
  errorCode: 'ECR-REG-0409',
  correlationId: 'test',

  // ⛔ Розширення лежать ПЛОСКО у верхньому рівні тіла (RFC 9457 §3.2) —
  // рівно так, як їх пише `ExceptionHandlingMiddleware`.
  registryEntryId: 42,
  references: 7,

  // ⛔ V-08: розклад за видами — сервер віддає лише ненульові.
  referenceKinds: { cells: 5, methodologyConstants: 2 },
};

interface Attempt {
  url: string;
  method: string;
}

/**
 * Замінює мережу і запам'ятовує спроби видалення.
 *
 * @param deleteResponse Що сервер відповідає на `DELETE`.
 */
function mockFetch(deleteResponse: () => Response): Attempt[] {
  const attempts: Attempt[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = (init?.method ?? 'GET').toUpperCase();

      if (method === 'DELETE') {
        attempts.push({ url, method });

        return deleteResponse();
      }

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
        return new Response(JSON.stringify([entry]), {
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

  return attempts;
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

/** Відкриває діалог підтвердження для єдиного запису в таблиці. */
async function openConfirm(): Promise<HTMLElement> {
  await screen.findByText('Kilogram');
  fireEvent.click(screen.getByRole('button', { name: 'Delete' }));

  // ⚠ Діалог Mantine доїжджає в DOM не в тому ж такті, що клацання
  // (перехід + портал), тому саме `find*`, а не `get*`: синхронна перевірка
  // бачила б лише кнопку РЯДКА і мовчки клацала б її вдруге.
  return screen.findByRole('dialog');
}

/** Кнопка підтвердження всередині діалогу, а не в рядку таблиці. */
function confirmButton(dialog: HTMLElement): HTMLElement {
  return within(dialog).getByRole('button', { name: 'Delete' });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Видалення запису довідника з інтерфейсу', () => {
  it('діалог називає запис і ставить фокус на «Cancel», а не на хрестик (X-23)', async () => {
    mockFetch(() => new Response(null, { status: 204 }));
    await loadCatalog('en', 'private');

    show();
    const dialog = await openConfirm();

    // ⛔ Доти заголовок був голим «Delete», а фокус падав на хрестик.
    expect(within(dialog).getByText('Delete entry "KG"?')).toBeDefined();
    await vi.waitFor(() => {
      expect(document.activeElement).toBe(within(dialog).getByRole('button', { name: 'Cancel' }));
    });
  });

  it('підтвердження шле DELETE саме на адресу цього запису', async () => {
    const attempts = mockFetch(() => new Response(null, { status: 204 }));
    await loadCatalog('en', 'private');

    show();
    const dialog = await openConfirm();

    fireEvent.click(confirmButton(dialog));

    await vi.waitFor(() => {
      expect(attempts).toHaveLength(1);
    });

    // ⛔ Головне твердження: метод і повна адреса. Сторож
    // `Кожна_дія_сервера_має_споживача_в_інтерфейсі` бачить літерал у коді, але
    // не бачить, ЩО саме відправлено; помилка в сегменті шляху лишилася б
    // непоміченою до першого клацання в браузері.
    expect(attempts[0]!.method).toBe('DELETE');
    expect(attempts[0]!.url).toBe('/api/v1/registries/UNITS/entries/42');
  });

  it('на 409 показує кількість посилань і дію «закрити датою» замість повтору', async () => {
    const attempts = mockFetch(
      () =>
        new Response(JSON.stringify(inUse), {
          status: 409,
          headers: { 'Content-Type': 'application/problem+json' },
        }),
    );

    await loadCatalog('en', 'private');

    show();
    const dialog = await openConfirm();

    fireEvent.click(confirmButton(dialog));

    // ⛔ Число посилань із `details.references` доходить до екрана. Це і є
    // мутаційна точка: прибрати гілку `entryReferences` у `RegistriesPage.tsx`
    // — і замість пояснення користувач побачить кнопку «видалити» ще раз.
    expect(await screen.findByText('7')).toBeDefined();

    // Текст відмови — серверний, уже локалізований каталогом помилок.
    expect(screen.getByText('Entry «KG» is referenced by 7 cells.')).toBeDefined();

    // ⛔ V-08: ХТО посилається — переліком за видами. Мутація: прибрати
    // `blockedBy` у `RegistriesPage.tsx` — рядків переліку немає.
    const kinds = within(dialog).getByRole('list', { name: 'Referenced by' });
    expect(within(kinds).getByText('Document cells: 5')).toBeDefined();
    expect(within(kinds).getByText('Methodology constants: 2')).toBeDefined();

    // ⛔ І пропонується саме вікно чинності, а не повтор: `POST …/validity` —
    // єдина дія, яка змінює стан справи, а не запит.
    expect(within(dialog).getByRole('button', { name: 'Valid' })).toBeDefined();

    // ⚠ Повтор не пропонується: підтвердження в діалозі недоступне
    // (`ConfirmModal`, X-23 — діалог той самий, стан «заблоковано»).
    expect(within(dialog).getByRole('button', { name: 'Delete' })).toHaveProperty('disabled', true);

    // Повторних спроб клієнт не робить сам: 4xx не повторюється.
    expect(attempts).toHaveLength(1);
  });
});
