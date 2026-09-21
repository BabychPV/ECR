import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { UnitsPage } from '../UnitsPage';
import { testTheme } from '@/test/render';

/**
 * Що екран одиниць отримав від переїзду на `DataTable` набору — і що при цьому
 * НЕ мало змінитися.
 *
 * ⛔ Переїзд — рефакторинг, тож половина цього набору стереже саме
 * незмінність: порядок при відкритті (розмірність, усередині неї код) і те,
 * що множник лишається рядком сервера, а не округленим числом. Друга половина
 * стереже нове: клацання по шапці сортує, і сортує ЧИСЛОВО.
 *
 * ⚠ «Показати ще» тут не перевіряється, бо екран його не має:
 * `GET /api/v1/units` віддає довідник одним масивом без курсора, тож
 * `total`/`onShowMore` таблиці не передаються і кнопка не малюється взагалі.
 * Перевірка кнопки, якої на екрані немає, стерегла б чужий компонент.
 */

const SeededStrings: Record<string, string> = {
  'units.title': 'Units of measure',
  'units.value': 'Value',
  'units.from': 'From',
  'units.to': 'To',
  'units.pickFrom': 'Pick the source unit first',
  'units.convert': 'Convert',
  'units.code': 'Unit',
  'units.dimension': 'Dimension',
  'units.factor': 'Factor to base',
  'units.offset': 'Offset to base',
  'units.base': 'base',
  'units.empty': 'No units registered',
  'units.emptyHint': 'Without units a formula cannot state what its numbers mean.',
  'common.cancel': 'Cancel',
};

/**
 * Фікстура підібрана так, щоб КОЖНЕ твердження нижче мало чим зламатися.
 *
 * ⚠ Порядок у масиві — НЕ той, у якому екран має показати рядки: інакше
 * прибирання сортування при відкритті лишилося б непоміченим.
 *
 * ⚠ Множники `0.5`, `2`, `9`, `10`: текстовий порядок цих рядків
 * (`'0.5…' < '10…' < '2…' < '9…'`) не збігається з числовим, тож колонка,
 * відсортована колатором замість `compareDecimals`, дасть інший перелік, а не
 * той самий.
 *
 * ⚠ Жоден множник не дорівнює одиниці: значка `base` в першій клітинці немає,
 * і текст клітинки — рівно код одиниці.
 */
const seededUnits = [
  {
    id: 3,
    code: 'c_half',
    dimensionId: 1,
    factorToBase: '0.5000000000',
    offsetToBase: '0.0000000000',
    dimensionCode: 'Mass',
  },
  {
    id: 2,
    code: 'b_ten',
    dimensionId: 1,
    factorToBase: '10.0000000000',
    offsetToBase: '0.0000000000',
    dimensionCode: 'Mass',
  },
  {
    id: 1,
    code: 'a_nine',
    dimensionId: 1,
    factorToBase: '9.0000000000',
    offsetToBase: '0.0000000000',
    dimensionCode: 'Mass',
  },
  {
    // ⚠ Інша розмірність, і код у ній АБЕТКОВО ПЕРШИЙ з усіх чотирьох: якщо
    // впорядкування загубить розмірність і лишить сам код, цей рядок поїде
    // нагору.
    id: 4,
    code: 'a_first',
    dimensionId: 2,
    factorToBase: '2.0000000000',
    offsetToBase: '0.0000000000',
    dimensionCode: 'Volume',
  },
];

/** Відповідь `GET /api/v1/units`: перелік, порожньо або відмова. */
type UnitsReply = { kind: 'list'; units: unknown[] } | { kind: 'fail' };

function mockApi(reply: UnitsReply): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';

      const json = (body: unknown, status = 200, type = 'application/json'): Response =>
        new Response(JSON.stringify(body), { status, headers: { 'Content-Type': type } });

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      }

      if (url.endsWith('/api/v1/me')) {
        return json({
          userId: 1,
          userName: 'bootstrap',
          language: 'en',
          permissions: [],
          isSimulation: false,
          denies: [],
          grants: {},
          mustChangePassword: false,
          simulatedForUserId: null,
        });
      }

      if (url.includes('/api/v1/units') && method === 'GET') {
        if (reply.kind === 'fail') {
          return json(
            {
              status: 500,
              title: 'Unit catalogue is unavailable',
              // ⚠ Код мусить бути з КАТАЛОГУ (`ErrorCodes.cs`), навіть у заглушці
              // відмови: сторож `ClientErrorCodeTests.Клієнт_не_згадує_кодів_яких
              // _немає_в_каталозі` читає ВЕСЬ `src/Ecr.Web/src` і продукт від тесту
              // не відрізняє. Вигаданий `ECR-UOM-0500` червонив гейти `test` і
              // `server` на спільній гілці — другий такий випадок за добу (перший
              // був `ECR-SYS-0499`). Родини `UOM-05xx` немає взагалі: п'ятисоті
              // коди каталогу — лише системні.
              errorCode: 'ECR-SYS-0500',
              detail: 'Unit catalogue is unavailable',
              correlationId: 'c-42',
            },
            500,
            'application/problem+json',
          );
        }

        return json(reply.units);
      }

      throw new Error(`Немає мока для ${method} ${url}`);
    }),
  );
}

async function show(reply: UnitsReply): Promise<void> {
  mockApi(reply);
  await loadCatalog('en', 'private');

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <UnitsPage />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/**
 * Коди одиниць у тому порядку, у якому вони намальовані.
 *
 * ⚠ Читається ПЕРША клітинка кожного рядка тіла, а не текст усієї таблиці:
 * порядок — це саме послідовність рядків, і перевіряти його через
 * `textContent` таблиці означало б зелене твердження на будь-якій перестановці
 * колонок.
 */
function codes(table: HTMLElement): string[] {
  return within(table)
    .getAllByRole('row')
    .slice(1)
    .map((row) => (row.querySelector('td')?.textContent ?? '').trim());
}

/** Шапка колонки як елемент `<th>` — з нього читається `aria-sort`. */
function header(table: HTMLElement, label: string): HTMLElement {
  const cell = within(table).getByRole('button', { name: label }).closest('th');

  expect(cell, `колонки «${label}» немає в шапці`).not.toBeNull();
  return cell as HTMLElement;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('UnitsPage на DataTable: порядок при відкритті не змінився', () => {
  it('рядки йдуть за розмірністю, усередині неї — за кодом', async () => {
    await show({ kind: 'list', units: seededUnits });

    const table = await screen.findByRole('table');
    await within(table).findByText('a_nine');

    /*
     * ⛔ Мутаційний доказ: приберіть `.sort(...)` у `rows` — і перелік вийде в
     * порядку відповіді сервера (`c_half, b_ten, a_nine, a_first`). Замініть
     * ключ на самий лише `code` — нагору поїде `a_first` із чужої розмірності.
     */
    expect(codes(table)).toEqual(['a_nine', 'b_ten', 'c_half', 'a_first']);

    // Жодна колонка ще не відсортована користувачем: стрілок немає.
    expect(header(table, 'Unit').getAttribute('aria-sort')).toBe('none');
  });

  it('множник показується рядком сервера, а не округленим числом', async () => {
    await show({ kind: 'list', units: seededUnits });

    const table = await screen.findByRole('table');
    await within(table).findByText('a_nine');

    /*
     * ⛔ Мутаційний доказ: приберіть `render` у колонки `factorToBase` — і
     * клітинка піде через `formatDecimal(raw, undefined, 3)` самої таблиці,
     * тобто `'0.5000000000'` стане `'0.5'`, а масштаб колонки зникне з екрана.
     * Саме заради цих знаків контракт і віддає `decimal` рядком (`e470777a`).
     */
    expect(within(table).getByText('0.5000000000')).toBeDefined();
    expect(within(table).getByText('10.0000000000')).toBeDefined();
  });
});

describe('UnitsPage на DataTable: сортування колонки — те, чого екран не мав', () => {
  it('клац по «Factor to base» впорядковує ЧИСЛОВО, а не текстом', async () => {
    await show({ kind: 'list', units: seededUnits });

    const table = await screen.findByRole('table');
    await within(table).findByText('a_nine');

    fireEvent.click(within(table).getByRole('button', { name: 'Factor to base' }));

    /*
     * ⛔ Мутаційний доказ: зніміть `num: true` з колонки `factorToBase` — і
     * порядок стане текстовим: `c_half, b_ten, a_first, a_nine`, тобто «десять»
     * опиниться між «пів» і «два». Літерали тут саме такі, щоб ця різниця була
     * видимою: числовий і текстовий порядки цих чотирьох рядків не збігаються.
     */
    expect(codes(table)).toEqual(['c_half', 'a_first', 'a_nine', 'b_ten']);
    expect(header(table, 'Factor to base').getAttribute('aria-sort')).toBe('ascending');

    // Другий клац — назад, від більшого до меншого.
    fireEvent.click(within(table).getByRole('button', { name: 'Factor to base' }));

    expect(codes(table)).toEqual(['b_ten', 'a_nine', 'a_first', 'c_half']);
    expect(header(table, 'Factor to base').getAttribute('aria-sort')).toBe('descending');
  });
});

describe('UnitsPage на DataTable: порожньо й відмова лишилися різними станами', () => {
  it('порожній довідник пояснює, що саме порожнє і чому це важливо', async () => {
    await show({ kind: 'list', units: [] });

    expect(await screen.findByText('No units registered')).toBeDefined();
    expect(
      screen.getByText('Without units a formula cannot state what its numbers mean.'),
    ).toBeDefined();

    // ⛔ Порожньо — це відсутність таблиці, а не таблиця з самою шапкою.
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('відмова сервера показується відмовою, а не «одиниць немає»', async () => {
    await show({ kind: 'fail' });

    /*
     * ⛔ Мутаційний доказ і головна причина, чому ця перевірка тут є: переїзд
     * прибрав зовнішню `AsyncBoundary`, і правило «відмова ≠ порожньо» тепер
     * тримає обгортка ВСЕРЕДИНІ таблиці. Приберіть проп `error` у `DataTable` —
     * і на місці відмови з'явиться «No units registered», тобто екран скаже,
     * що довідник порожній, хоча його просто не вдалося прочитати.
     */
    const alert = await screen.findByRole('alert');

    expect(within(alert).getByText('Unit catalogue is unavailable')).toBeDefined();
    expect(screen.queryByText('No units registered')).toBeNull();
  });
});
