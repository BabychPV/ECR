import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CreateMappingModal } from '@/features/mapping/CreateMappingModal';
import { testTheme } from '@/test/render';

/**
 * Вибір імені з каталогу PI AF для форми мапінгу (`ФВ-13.13`).
 *
 * ⛔ Що саме перевіряється, і чому кожне з цього не видно з екрана:
 *  1. **Право.** Кнопки немає без `Integration.Manage` — те саме право, що
 *     вимагає сервер на `GET /data-sources/{id}/catalog`.
 *  2. **Лінива підвантага.** Розгортання елемента читає дочірні САМЕ його
 *     шляхом. Без `path` сервер віддає корені, і розгорнутий вузол показав би
 *     сам себе — на екрані це виглядає як дивна ієрархія в джерелі, а не як
 *     дефект клієнта.
 *  3. **`503`/`502` — стан ДЖЕРЕЛА, не помилка форми.** Під полем шляху така
 *     відмова читається як «ти ввела не те», а виправити її в полі неможливо.
 *  4. **Вибір атрибута** доводить справу до кінця: підставляє шлях у поле і
 *     закриває вікно. Каталог, який показує і не підставляє, не робить нічого.
 *
 * ⚠ Шляхи у фікстурах навмисно без зворотних скісних (`Plant/Unit1`): у
 * справжньому AF вони саме зі скісними, але тут перевіряється ПЕРЕДАЧА шляху
 * туди й назад, а не його форма — а екранування скісних у літералі тесту
 * породило б власний клас помилок, яких у продукті немає.
 */

/** Кореневий елемент ієрархії. */
const Unit1 = {
  code: 'Unit1',
  displayName: 'Unit 1',
  path: 'Plant/Unit1',
  kind: 'Element',
  dataType: 'Element',
  unitSymbol: null,
};

/** Другий кореневий — приходить лише другою сторінкою. */
const Unit2 = {
  code: 'Unit2',
  displayName: 'Unit 2',
  path: 'Plant/Unit2',
  kind: 'Element',
  dataType: 'Element',
  unitSymbol: null,
};

/** Дочірній елемент першого вузла. */
const Boiler = {
  code: 'Boiler',
  displayName: 'Boiler A',
  path: 'Plant/Unit1/Boiler',
  kind: 'Element',
  dataType: 'Element',
  unitSymbol: null,
};

/** Атрибут першого вузла — кінцевий вузол, який і мапиться. */
const Co2 = {
  code: 'CO2',
  displayName: 'CO2 flow',
  path: 'Plant/Unit1|CO2',
  kind: 'Attribute',
  dataType: 'Float64',
  unitSymbol: 't/h',
};

/** Параметри, з якими клієнт звернувся по каталог. */
interface CatalogCall {
  readonly path: string | null;
  readonly search: string | null;
  readonly cursor: string | null;
  readonly limit: string | null;
}

const calls: CatalogCall[] = [];
let meCalls = 0;

/** Що віддає сервер на запит каталогу; задається кожним тестом окремо. */
let respond: (url: URL) => Response = () => page([]);

function json(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

/** Сторінка каталогу у формі контракту. */
function page(items: readonly unknown[], nextCursor: string | null = null): Response {
  return json({ items, nextCursor }, 200);
}

/** Локалізоване сервером речення відмови — показуване лише за `messageKey`. */
const OutageDetail = 'Data source "PI-MAIN" is unavailable, so its catalog could not be read.';

/**
 * `503` у формі, якою його віддає `ExceptionHandlingMiddleware`.
 *
 * ⚠ Коди тут — з `ErrorCodes.cs` (`SourceUnavailable`), а не вигадані:
 * `ClientErrorCodeTests` читає і тести теж.
 */
function outage(status: number, errorCode: string): Response {
  return json(
    {
      title: 'Source unavailable',
      status,
      detail: OutageDetail,
      errorCode,
      correlationId: 'cid-test',
      messageKey: 'err.ECR-INT-0503.catalogUnavailable',
      code: 'PI-MAIN',
      timeoutSeconds: 10,
    },
    status,
  );
}

const DataSourceId = 7;

function stubFetch(permissions: readonly string[]): void {
  calls.length = 0;
  meCalls = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string) => {
      const url = new URL(String(input), 'http://localhost');

      if (url.pathname === '/api/v1/me') {
        meCalls += 1;

        return json(
          { userId: 1, userName: 'tester', language: 'en', permissions, isSimulation: false },
          200,
        );
      }

      if (url.pathname === `/api/v1/data-sources/${DataSourceId}/catalog`) {
        calls.push({
          path: url.searchParams.get('path'),
          search: url.searchParams.get('search'),
          cursor: url.searchParams.get('cursor'),
          limit: url.searchParams.get('limit'),
        });

        return respond(url);
      }

      return json({ id: 1 }, 200);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <CreateMappingModal
          sourceEntityId={5}
          dataSourceId={DataSourceId}
          opened
          onClose={() => {}}
          onCreated={() => {}}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Каталог у тестах не завантажений — `t()` віддає ключ у дужках. */
const OpenLabel = '⟦mapping.catalogOpen⟧';
const ExpandLabel = '⟦mapping.catalogExpand⟧';
const MoreLabel = '⟦mapping.catalogMore⟧';
const RetryLabel = '⟦common.retry⟧';
const SearchLabel = '⟦mapping.catalogSearch⟧';
const SearchApplyLabel = '⟦mapping.catalogSearchApply⟧';
const FieldLabel = '⟦mapping.createField⟧';

/** Дає React Query застосувати вже отримані відповіді. */
async function settle(): Promise<void> {
  await act(async () => {
    await new Promise((resolve) => {
      setTimeout(resolve, 0);
    });
  });
}

async function openCatalog(): Promise<void> {
  fireEvent.click(await screen.findByRole('button', { name: OpenLabel }));
}

/** Розгортає перший елемент дерева. */
function expandFirst(): void {
  fireEvent.click(screen.getAllByRole('button', { name: ExpandLabel })[0] as HTMLElement);
}

beforeEach(() => {
  respond = () => page([]);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Каталог PI AF у формі мапінгу (ФВ-13.13)', () => {
  it('кнопка каталогу є з правом Integration.Manage і зникає без нього', async () => {
    stubFetch(['Integration.Manage']);
    show();

    // Профіль із правом — кнопка знаходиться саме після його приходу.
    expect(await screen.findByRole('button', { name: OpenLabel })).toBeDefined();

    cleanup();
    stubFetch([]);
    show();

    // ⛔ Той самий шлях і та сама затримка, що у випадку вище: профіль
    // прийшов і застосований. Отже відсутність кнопки — не гонка, а перевірка
    // права. Мутація «право не перевіряється» малює кнопку ще ДО профілю, і
    // цей рядок червоніє.
    await waitFor(() => {
      expect(meCalls).toBe(1);
    });
    await settle();

    expect(screen.queryByRole('button', { name: OpenLabel })).toBeNull();
  });

  it('розгортання елемента читає дочірні САМЕ його шляхом', async () => {
    stubFetch(['Integration.Manage']);

    respond = (url) => {
      const path = url.searchParams.get('path');

      if (path === null) return page([Unit1]);
      if (path === Unit1.path) return page([Boiler, Co2]);

      return page([]);
    };

    show();
    await openCatalog();

    expect(await screen.findByText('Unit 1')).toBeDefined();

    // Перший запит — кореневий рівень: без `path` і з явною межею сторінки.
    expect(calls[0]?.path).toBeNull();
    expect(calls[0]?.limit).toBe('50');

    expandFirst();

    // ⛔ Мутаційний доказ: якщо розгортання не передасть `path`, сервер
    // віддасть корені, і ані «Boiler A», ані «CO2 flow» тут не з'являться.
    expect(await screen.findByText('Boiler A')).toBeDefined();
    expect(screen.getByText('CO2 flow')).toBeDefined();
    expect(calls.map((call) => call.path)).toContain(Unit1.path);
  });

  it('атрибут показаний з одиницею джерела', async () => {
    stubFetch(['Integration.Manage']);
    respond = (url) => (url.searchParams.get('path') === null ? page([Unit1]) : page([Co2]));

    show();
    await openCatalog();
    await screen.findByText('Unit 1');
    expandFirst();

    await screen.findByText('CO2 flow');

    // ⚠ Одиниця джерела — те, за чим ловлять мовчазну зміну на межі
    // інтеграції (`ECR-INT-0422`); побачити її треба ДО заведення мапінгу.
    expect(screen.getByText('t/h')).toBeDefined();
  });

  it('вибір атрибута підставляє шлях у форму і закриває каталог', async () => {
    stubFetch(['Integration.Manage']);
    respond = (url) => (url.searchParams.get('path') === null ? page([Unit1]) : page([Co2]));

    show();
    await openCatalog();
    await screen.findByText('Unit 1');
    expandFirst();

    fireEvent.click(await screen.findByRole('button', { name: 'CO2 flow' }));

    // ⛔ Мутаційний доказ: каталог, який показує і не підставляє, лишає поле
    // порожнім — і людина однаково набирає шлях руками.
    await waitFor(() => {
      expect((screen.getByLabelText(FieldLabel) as HTMLInputElement).value).toBe(Co2.path);
    });

    await waitFor(() => {
      expect(screen.queryByLabelText(SearchLabel)).toBeNull();
    });
  });

  it('503 — стан «джерело не відповідає», а не помилка під полем форми', async () => {
    stubFetch(['Integration.Manage']);
    respond = () => outage(503, 'ECR-INT-0503');

    show();
    await openCatalog();

    // ⛔ Мутаційний доказ: відмова джерела показана ОКРЕМИМ станом каталогу.
    await waitFor(() => {
      expect(document.querySelector('[data-catalog-state="unavailable"]')).not.toBeNull();
    });

    // Причину називає сервер — вона локалізована (`messageKey`).
    expect(screen.getByText(OutageDetail)).toBeDefined();

    // ⛔ І НЕ під полем джерельного шляху: причина не в ньому, і виправити її
    // в полі неможливо. Мутація «показати як помилку форми» ставить на поле
    // `aria-invalid` і опис помилки — обидва рядки червоніють.
    // ⚠ Саме `'false'`, а не відсутність атрибута: Mantine ставить
    // `aria-invalid` завжди, і «немає атрибута» тут не буває ніколи —
    // перевіряти треба ЗНАЧЕННЯ. Опису помилки при цьому немає зовсім
    // (`aria-describedby` з'являється разом із ним).
    const field = screen.getByLabelText(FieldLabel);
    expect(field.getAttribute('aria-invalid')).toBe('false');
    expect(field.getAttribute('aria-describedby')).toBeNull();

    // Решта інтерфейсу жива: пошук доступний, форму мапінгу не заблоковано.
    expect((screen.getByLabelText(SearchLabel) as HTMLInputElement).disabled).toBe(false);

    const before = calls.length;
    fireEvent.click(screen.getByRole('button', { name: RetryLabel }));

    await waitFor(() => {
      expect(calls.length).toBeGreaterThan(before);
    });
  });

  it('502 — та сама подача: джерело відмовило в автентифікації', async () => {
    stubFetch(['Integration.Manage']);
    respond = () => outage(502, 'ECR-INT-0502');

    show();
    await openCatalog();

    await waitFor(() => {
      expect(document.querySelector('[data-catalog-state="unavailable"]')).not.toBeNull();
    });

    expect(screen.getByRole('button', { name: RetryLabel })).toBeDefined();
    expect(screen.getByLabelText(FieldLabel).getAttribute('aria-invalid')).toBe('false');
  });

  it('курсор дочитує наступну сторінку рівня, не гублячи попередню', async () => {
    stubFetch(['Integration.Manage']);

    respond = (url) =>
      url.searchParams.get('cursor') === null ? page([Unit1], '50') : page([Unit2]);

    show();
    await openCatalog();
    await screen.findByText('Unit 1');

    fireEvent.click(screen.getByRole('button', { name: MoreLabel }));

    expect(await screen.findByText('Unit 2')).toBeDefined();
    expect(screen.getByText('Unit 1')).toBeDefined();
    expect(calls.map((call) => call.cursor)).toContain('50');
  });

  it('пошук іде на сервер параметром search', async () => {
    stubFetch(['Integration.Manage']);

    respond = (url) => (url.searchParams.get('search') === 'CO2' ? page([Co2]) : page([Unit1]));

    show();
    await openCatalog();
    await screen.findByText('Unit 1');

    fireEvent.change(screen.getByLabelText(SearchLabel), { target: { value: 'CO2' } });
    fireEvent.click(screen.getByRole('button', { name: SearchApplyLabel }));

    expect(await screen.findByText('CO2 flow')).toBeDefined();
    expect(calls.map((call) => call.search)).toContain('CO2');
  });

  it('порожній результат пошуку відрізняється від порожнього каталогу (L10)', async () => {
    stubFetch(['Integration.Manage']);
    respond = (url) => (url.searchParams.get('search') === null ? page([Unit1]) : page([]));

    show();
    await openCatalog();
    await screen.findByText('Unit 1');

    fireEvent.change(screen.getByLabelText(SearchLabel), { target: { value: 'нема' } });
    fireEvent.click(screen.getByRole('button', { name: SearchApplyLabel }));

    await waitFor(() => {
      expect(document.querySelector('[data-catalog-state="search-empty"]')).not.toBeNull();
    });

    // ⚠ «Фільтр нічого не знайшов» дає дію — зняти фільтр; «каталог порожній»
    // її не має, бо знімати нічого.
    expect(document.querySelector('[data-catalog-state="empty"]')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: '⟦mapping.catalogSearchClear⟧' }));

    expect(await screen.findByText('Unit 1')).toBeDefined();
  });

  it('порожній каталог пояснює, чому порожньо (L10)', async () => {
    stubFetch(['Integration.Manage']);
    respond = () => page([]);

    show();
    await openCatalog();

    await waitFor(() => {
      expect(document.querySelector('[data-catalog-state="empty"]')).not.toBeNull();
    });

    expect(screen.getByText('⟦mapping.catalogEmptyHint⟧')).toBeDefined();
  });

  it('каталог закритий за замовчуванням і нічого не читає (L2)', async () => {
    stubFetch(['Integration.Manage']);
    show();

    await screen.findByRole('button', { name: OpenLabel });
    await settle();

    // ⛔ Запит у ЧУЖУ систему з межею очікування 10 с не робиться, доки вікно
    // не відкрито: інакше кожне відкриття форми мапінгу смикало б PI.
    expect(calls).toHaveLength(0);
    expect(screen.queryByLabelText(SearchLabel)).toBeNull();
  });
});
