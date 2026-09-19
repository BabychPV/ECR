import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { formatDate } from '@/shared/format';
import { RegistriesPage } from '../RegistriesPage';
import { testTheme } from '@/test/render';

/**
 * `UI-07`: вікно чинності запису довідника — читабельне на екрані, точне в
 * розмітці, і «діє безстроково» не прикидається «значення немає».
 *
 * ⛔ Навіщо окремо від `shared/ui/__tests__/Timestamp.test.tsx`. Той доводить
 * поведінку КОМПОНЕНТА і лишиться зеленим, якщо жодна сторінка його не
 * викличе — рівно в цьому стані й прожив модуль `shared/format`: написаний,
 * протестований, без жодного продуктового споживача. Тут перевіряється інше
 * твердження: ЦЯ колонка ЦІЄЇ сторінки показує дати через набір.
 *
 * ⛔ Три твердження, і жодне не випливає з решти:
 *   1. видимий текст НЕ дорівнює сирому входу (`2024-01-01` → «Jan 1, 2024»);
 *   2. точне значення лишилося в розмітці (`<time dateTime>`) — тобто нічого
 *      не втрачено для копіювання, звірки й локатора e2e;
 *   3. порожня межа показується НЕ тире.
 *
 * ⚠ Очікуваний текст береться з `shared/format`, а НЕ пишеться літералом:
 * літерал був би перевіркою версії ICU у Node і червонів би від оновлення
 * середовища, нічого не зламавши.
 *
 * ⚠ Форми відповідей звірені з викликами сторінки, а не вгадані:
 * `apiFetch<RegistryDefDto[]>('/api/v1/registries')` і
 * `apiFetch<RegistryEntryDto[]>('…/entries')` — МАСИВИ, не `{ items: [...] }`.
 * Заглушка форми `{ items }` валить сторінку на `registries.data?.find is not
 * a function`, і набір падав би за таймаутом зовсім не з тієї причини, яку
 * перевіряє. Вибір довідника — через `useUrlState('code')`, тобто адреса
 * `/admin/registries?code=UNITS`.
 */

const SeededStrings: Record<string, string> = {
  'registries.title': 'Registries',
  'registries.pick': 'Pick a registry',
  'registries.code': 'Code',
  'registries.name': 'Name',
  'registries.parent': 'Parent',
  'registries.validity': 'Valid',
  'registries.search': 'Search',
  'registries.searchPlaceholder': 'Filter by code or name',
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
  isTemporal: true,
  isHierarchical: false,
  sourceKind: 'Master',
};

/** Початок вікна чинності — календарна дата (`Format: date`). */
const ValidFrom = '2024-01-01';

/** Кінець вікна чинності — теж календарна дата, не момент. */
const ValidTo = '2025-01-01';

/** Запис із обома межами: чинний рівно всередині вікна. */
const Bounded = {
  id: 1,
  code: 'KG',
  display: 'Kilogram',
  parentEntryId: 7,
  validFrom: ValidFrom,
  validTo: ValidTo,
};

/** Той самий запис без обох меж: чинний від початку й безстроково. */
const Unbounded = {
  ...Bounded,
  id: 2,
  code: 'TN',
  display: 'Tonne',
  validFrom: null,
  validTo: null,
};

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

/** Вузол `<time>` із точно цим `dateTime`. */
function timeNode(iso: string): HTMLElement | null {
  return document.querySelector(`time[datetime="${iso}"]`);
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('RegistriesPage: вікно чинності запису довідника', () => {
  it(
    'обидві межі читабельні на екрані й точні в розмітці — без вигаданої години',
    async () => {
      mockFetch([Bounded]);
      show();

      await screen.findByText('Kilogram', {}, { timeout: SlowEnvTimeout });

      const from = timeNode(ValidFrom);
      expect(from, 'початок вікна чинності не намальовано елементом <time>').not.toBeNull();
      expect(from?.textContent).toBe(formatDate(ValidFrom));

      const to = timeNode(ValidTo);
      expect(to, 'кінець вікна чинності не намальовано елементом <time>').not.toBeNull();
      expect(to?.textContent).toBe(formatDate(ValidTo));

      /*
       * ⛔ Перша половина: видимий текст НЕ дорівнює сирому входу. Без неї
       * набір лишився б зеленим на форматувальнику, що повертає вхід, —
       * тобто саме на тому стані, який ця зміна й усуває (склейка рядком
       * друкувала `2024-01-01` як є).
       */
      expect(from?.textContent).not.toBe(ValidFrom);
      expect(to?.textContent).not.toBe(ValidTo);

      /*
       * ⛔ Друга половина: точне значення нікуди не діло́ся — воно лишилось
       * у розмітці, придатне до копіювання, звірки й локатора e2e. Саме це
       * твердження відрізняє обраний варіант від `formatDate()` всередині
       * склейки: там текст теж став би читабельним, але точному значенню не
       * було б ДЕ лишитись — рядок не має атрибутів.
       */
      expect(from?.getAttribute('title')).toBe(ValidFrom);
      expect(to?.getAttribute('title')).toBe(ValidTo);

      /*
       * ⚠ `dateOnly`: контракт віддає обидві межі як `Format: date`, тобто
       * години в даних немає взагалі. «12:00 AM» приписало б їй точність,
       * якої сервер не надсилав.
       */
      expect(from?.textContent).not.toMatch(/\d{1,2}:\d{2}/);
      expect(to?.textContent).not.toMatch(/\d{1,2}:\d{2}/);

      // ⚠ Роздільник вікна не з'їдено розбиранням склейки: клітинка й далі
      // читається одним діапазоном «Jan 1, 2024 — Jan 1, 2025».
      const cell = from?.closest('td');
      expect(cell?.textContent).toBe(
        `${formatDate(ValidFrom)} — ${formatDate(ValidTo)}`,
      );
    },
    SlowEnvTimeout,
  );

  it(
    'межі немає — «…», а не тире: запис чинний далі, а не «без значення»',
    async () => {
      mockFetch([Unbounded]);
      show();

      await screen.findByText('Tonne', {}, { timeout: SlowEnvTimeout });

      const empties = document.querySelectorAll('[data-timestamp="none"]');

      // Дві порожні межі — початок і кінець вікна чинності одного запису.
      expect(empties).toHaveLength(2);

      /*
       * ⛔ Саме це твердження і є предметом рішення: дефолт `Timestamp` —
       * тире, і воно тут НЕ використане. Приберіть `fallback={Unbounded}` у
       * `RegistriesPage.tsx` — обидві межі стануть тире, і цей `expect`
       * почервоніє («expected '—' to be '…'»).
       *
       * ⚠ `queryByText('—')` тут НЕ годиться як доказ: сусідня клітинка
       * «батьківський запис» друкує тире власним `?? '—'`, і перевірка
       * ловила б її замість меж вікна. Селектор за `data-timestamp` дивиться
       * рівно на те, що намалював `Timestamp`.
       */
      for (const node of empties) {
        expect(node.textContent).toBe('…');
      }

      // Порожня межа — не `<time>`: моменту немає, і машинозчитуваного
      // значення теж немає чому взятися.
      expect(document.querySelector('time')).toBeNull();
    },
    SlowEnvTimeout,
  );
});
