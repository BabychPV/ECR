import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { TemplatesPage } from '@/pages/admin/TemplatesPage';
import { testTheme } from '@/test/render';

/**
 * Що екран шаблонів отримав від переїзду на `DataTable` набору — і що при цьому
 * НЕ мало змінитися.
 *
 * ⛔ Сира `<Table striped className="ecr-sticky-head">` не вміла сортування
 * ВЗАГАЛІ: у її `<Table.Th>` не було ні кнопки, ні `aria-sort`, тож колонку
 * «Код» не можна було впорядкувати ні мишею, ні читалкою. Саме це тут і
 * стережеться — на незміненому файлі перший опис падає на тому, що кнопки в
 * шапці немає.
 *
 * ⛔ Друге твердження важливіше за перше, хоч і виглядає дрібнішим. Версії
 * бралися `versionQueries[index]` — ПО ПОЗИЦІЇ рядка в масиві сервера. Поки
 * порядок рядків незмінний, це працює; щойно шапка почала переставляти рядки,
 * та сама формула віддала б рядку ЧУЖІ версії, і посилання вело б на версію
 * іншого шаблону. Дефект даних, а не розмітки: на екрані він виглядає як
 * звичайне посилання.
 *
 * ⚠ Каталог у тестах не вантажиться, тож `t()` чесно віддає `⟦ключ⟧`. Закриваюча
 * дужка в регекспі обов'язкова: без неї `⟦templates.code⟧` не відрізнити від
 * `⟦templates.codeHint⟧` із діалогу створення.
 */

/**
 * ⚠ Порядок у масиві — НЕ абетковий: `BRAVO` віддається першим. Інакше
 * «відсортовано за зростанням» не відрізнялося б від «як прийшло з сервера», і
 * перший клац по шапці нічого б не доводив.
 */
const templates = {
  items: [
    { id: 7, code: 'BRAVO', nameL10n: { values: { en: 'Bravo' } } },
    { id: 3, code: 'ALPHA', nameL10n: { values: { en: 'Alpha' } } },
  ],
  nextCursor: null,
  totalCount: 2,
};

/**
 * Версії РІЗНІ в кожного шаблону — і це не декор фікстури.
 *
 * ⚠ Номер версії, її ідентифікатор і ревізія не збігаються ніде, тож
 * переплутані переліки видно з будь-якої клітинки: `9.9 · r2` біля `BRAVO`
 * означає, що рядок узяв чужу відповідь.
 */
const versionsByTemplate: Record<string, unknown> = {
  '7': {
    items: [{ id: 70, version: '1.0', status: 'Published', presentationRevision: 1 }],
    nextCursor: null,
    totalCount: 1,
  },
  '3': {
    items: [{ id: 30, version: '9.9', status: 'Draft', presentationRevision: 2 }],
    nextCursor: null,
    totalCount: 1,
  },
};

/** Що відповідає `GET /api/v1/templates`: перелік, порожньо або відмова. */
type TemplatesReply = { kind: 'list' } | { kind: 'empty' } | { kind: 'fail' };

function mockApi(reply: TemplatesReply): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      const json = (body: unknown, status = 200, type = 'application/json'): Response =>
        new Response(JSON.stringify(body), { status, headers: { 'Content-Type': type } });

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: [],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      // ⚠ Порядок гілок значущий: адреса версій МІСТИТЬ `/api/v1/templates` як
      // префікс. Зворотний порядок віддав би перелік шаблонів у відповідь на
      // запит версій — і тест падав би не з тієї причини, яку перевіряє.
      const versionsOf = /\/api\/v1\/templates\/(\d+)\/versions/.exec(url);

      if (versionsOf !== null) {
        return json(versionsByTemplate[versionsOf[1] ?? ''] ?? { items: [], totalCount: 0 });
      }

      if (url.includes('/api/v1/templates')) {
        if (reply.kind === 'fail') {
          return json(
            {
              status: 500,
              title: 'Template catalogue is unavailable',
              detail: 'Template catalogue is unavailable',
              // ⛔ Код — із каталогу (`Ecr.Domain/Errors/ErrorCodes.cs`), і саме
              // системний. Правдоподібний код домену валить сторожа
              // `ClientErrorCodeTests`: він читає ВЕСЬ `src/Ecr.Web/src` і тесту
              // від продукту не відрізняє, а родини «шаблони, 500» не існує —
              // п'ятисоті коди в каталозі лише системні.
              errorCode: 'ECR-SYS-0500',
              correlationId: 'c-templates-1',
            },
            500,
            'application/problem+json',
          );
        }

        return json(
          reply.kind === 'empty'
            ? { items: [], nextCursor: null, totalCount: 0 }
            : templates,
        );
      }

      return json(null);
    }),
  );
}

function show(reply: TemplatesReply): void {
  mockApi(reply);

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter>
        <QueryClientProvider client={client}>
          <TemplatesPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/**
 * Коди шаблонів у тому порядку, у якому вони намальовані.
 *
 * ⚠ Читається ПЕРША клітинка кожного рядка тіла, а не текст усієї таблиці:
 * порядок — це послідовність рядків, і перевірка через `textContent` таблиці
 * лишалася б зеленою на будь-якій перестановці колонок.
 */
function codes(table: HTMLElement): string[] {
  return within(table)
    .getAllByRole('row')
    .slice(1)
    .map((row) => (row.querySelector('td')?.textContent ?? '').trim());
}

/** Шапка колонки як `<th>` — з нього читається `aria-sort`. */
function header(table: HTMLElement, name: RegExp): HTMLElement {
  const cell = within(table).getByRole('button', { name }).closest('th');

  expect(cell, `колонки ${String(name)} немає в шапці`).not.toBeNull();

  return cell as HTMLElement;
}

/**
 * Посилання на версію в рядку з заданим кодом шаблону.
 *
 * ✎ 2026-09-21 (`UI-09`): у рядку стало ДВА посилання — код шаблону тепер веде
 * на його картку (`/admin/templates/{id}`), і `getByRole('link')` без уточнення
 * падав би на «знайдено два». Вибірка за `/versions/` у `href` — не
 * послаблення: твердження нижче й далі звіряє ТОЧНИЙ `href` версії, тобто
 * ловить рівно ту мутацію, заради якої написане (рядок бере версії сусіда).
 */
function versionLinkOf(table: HTMLElement, code: string): HTMLAnchorElement {
  const row = within(table)
    .getAllByRole('row')
    .slice(1)
    .find((candidate) => (candidate.querySelector('td')?.textContent ?? '').trim() === code);

  expect(row, `рядка «${code}» немає в таблиці`).toBeDefined();

  const link = within(row as HTMLElement)
    .getAllByRole('link')
    .find((candidate) => (candidate.getAttribute('href') ?? '').includes('/versions/'));

  expect(link, `у рядку «${code}» немає посилання на версію`).toBeDefined();

  return link as HTMLAnchorElement;
}

/** Посилання на КАРТКУ шаблону в рядку з заданим кодом (`UI-09`). */
function cardLinkOf(table: HTMLElement, code: string): HTMLAnchorElement {
  const link = within(table).getByRole('link', { name: code });

  return link as HTMLAnchorElement;
}

const CodeHeader = /templates\.code⟧/;

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('TemplatesPage на DataTable: шапка сортує — того сира таблиця не вміла', () => {
  it('колонка коду оголошує стан сортування через `aria-sort`', async () => {
    show({ kind: 'list' });

    const table = await screen.findByRole('table');
    await within(table).findByText('BRAVO');

    /*
     * ⛔ Мутаційний доказ: `sortable: false` на колонці `code` — і `<th>`
     * лишається без `aria-sort` зовсім (`HeaderCell` ставить атрибут лише
     * сортовній колонці), а кнопки в шапці немає, тож `getByRole('button')`
     * усередині `header()` не знаходить нічого.
     */
    expect(header(table, CodeHeader).getAttribute('aria-sort')).toBe('none');

    // Порядок при відкритті — рівно той, що віддав сервер: набір нічого не
    // переставляє, доки по шапці не клацнули.
    expect(codes(table)).toEqual(['BRAVO', 'ALPHA']);
  });

  it('клац по шапці переставляє рядки, другий — розвертає порядок', async () => {
    show({ kind: 'list' });

    const table = await screen.findByRole('table');
    await within(table).findByText('BRAVO');

    fireEvent.click(within(table).getByRole('button', { name: CodeHeader }));

    expect(codes(table)).toEqual(['ALPHA', 'BRAVO']);
    expect(header(table, CodeHeader).getAttribute('aria-sort')).toBe('ascending');

    fireEvent.click(within(table).getByRole('button', { name: CodeHeader }));

    expect(codes(table)).toEqual(['BRAVO', 'ALPHA']);
    expect(header(table, CodeHeader).getAttribute('aria-sort')).toBe('descending');
  });

  it('після перестановки рядок несе ВЛАСНІ версії, а не версії сусіда', async () => {
    show({ kind: 'list' });

    const table = await screen.findByRole('table');

    /*
     * ⚠ Чекаємо на КОД шаблону, а не на номер версії: очікування саме номера
     * зробило б найцікавішу мутацію («усі рядки беруть версії першого запиту»)
     * падінням очікування, а не падінням ТВЕРДЖЕННЯ про `href` нижче.
     */
    await within(table).findByText('BRAVO');

    // ⚠ Чотири, а не два (`UI-09`): по два посилання на рядок — картка шаблону
    // і його версія. Число лишається точним навмисно: «принаймні два» не
    // помітило б зниклої колонки версій.
    await waitFor(() => {
      expect(within(table).getAllByRole('link')).toHaveLength(4);
    });

    fireEvent.click(within(table).getByRole('button', { name: CodeHeader }));

    /*
     * ⛔ Мутаційний доказ: поверніть пошук версій по ПОЗИЦІЇ рядка
     * (`versionQueries[index]` у `render` колонки замість `versionsOf.get(
     * template.id)`) — і після сортування `ALPHA` (id 3) покаже посилання
     * `/admin/templates/3/versions/70`, тобто ідентифікатор версії сусіднього
     * шаблону. Href лишиться правдоподібним, і без цієї перевірки дефект видно
     * лише за переходом.
     */
    expect(versionLinkOf(table, 'ALPHA').getAttribute('href')).toBe(
      '/admin/templates/3/versions/30',
    );
    expect(versionLinkOf(table, 'BRAVO').getAttribute('href')).toBe(
      '/admin/templates/7/versions/70',
    );
  });

  it('код шаблону веде на ЙОГО картку, а не на картку сусіда (UI-09)', async () => {
    show({ kind: 'list' });

    const table = await screen.findByRole('table');
    await within(table).findByText('BRAVO');

    fireEvent.click(within(table).getByRole('button', { name: CodeHeader }));

    /*
     * ⛔ Та сама мутація, що й для версій, лише на іншій колонці: адреса
     * картки будується з `template.id` РЯДКА. Підстановка позиції рядка
     * (`items[index].id`) після сортування дала б `ALPHA` адресу `…/7` —
     * правдоподібну й чужу.
     */
    expect(cardLinkOf(table, 'ALPHA').getAttribute('href')).toBe('/admin/templates/3');
    expect(cardLinkOf(table, 'BRAVO').getAttribute('href')).toBe('/admin/templates/7');
  });
});

describe('TemplatesPage на DataTable: порожньо й відмова лишилися різними станами', () => {
  it('порожній перелік пояснює, що саме порожнє', async () => {
    show({ kind: 'empty' });

    expect(await screen.findByText(/templates\.empty⟧/)).toBeDefined();

    // ⛔ Порожньо — це відсутність таблиці, а не таблиця з самою шапкою.
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('відмова сервера показується відмовою, а не «шаблонів немає»', async () => {
    show({ kind: 'fail' });

    /*
     * ⛔ Головна причина, чому ця перевірка тут є: переїзд ПРИБРАВ зовнішню
     * `AsyncBoundary`, і правило «відмова ≠ порожньо» тепер тримає обгортка
     * ВСЕРЕДИНІ таблиці. Приберіть проп `error` у `DataTable` — і на місці
     * відмови з'явиться «⟦templates.empty⟧», тобто екран скаже, що шаблонів
     * немає, хоча їх просто не вдалося прочитати.
     */
    const alert = await screen.findByRole('alert');

    expect(within(alert).getByText('Template catalogue is unavailable')).toBeDefined();
    expect(screen.queryByText(/templates\.empty⟧/)).toBeNull();
  });
});
