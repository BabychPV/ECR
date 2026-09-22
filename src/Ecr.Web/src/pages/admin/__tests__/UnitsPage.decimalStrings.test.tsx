import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { UnitsPage } from '../UnitsPage';
import { testTheme } from '@/test/render';

/**
 * `decimal` у відповідях і запитах API їде РЯДКОМ (`e470777a`): `JSON.parse`
 * проганяє число через IEEE-754 і губить 16-й знак ще до того, як до нього
 * можна дотягнутися. Цей набір стереже обидва боки цієї зміни саме на екрані
 * одиниць — там, де десяткові і показуються, і вводяться.
 *
 * ⛔ Обидва твердження нижче зелені й на неправильному коді, якщо перевіряти
 * їх через `Number`: `Number('1.0000000000') === 1` і
 * `Number('0.4535923700000000') === 0.45359237` — правда. Тому фікстури тут
 * навмисно з масштабом колонки й із хвостом за 15-м знаком.
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
  'units.new': 'New unit',
  'units.newCode': 'Code',
  'units.newCodeHint': 'Latin letters, digits and underscore; cannot be changed later.',
  'units.symbol': 'Symbol',
  'units.symbolHint': 'Shown next to values, e.g. "kg".',
  'units.name': 'Name',
  'units.factorHint': "Multiplier to the dimension's base unit.",
  'units.offsetHint': 'Only nonzero for temperature units.',
  'units.created': 'Unit created.',
  'common.cancel': 'Cancel',
};

/** Множник, що відрізняється від базового на 17-му знаку — `Number` їх ототожнює. */
const AlmostOne = '1.0000000000000000001';

const seededUnits = [
  {
    id: 1,
    code: 'kg',
    dimensionId: 1,
    // ⚠ Масштаб колонки: те саме число, що й `1`, просто надруковане повністю.
    factorToBase: '1.0000000000',
    offsetToBase: '0.0000000000',
    dimensionCode: 'Mass',
  },
  {
    id: 2,
    code: 'g',
    dimensionId: 1,
    factorToBase: '0.0010000000',
    offsetToBase: '0.0000000000',
    dimensionCode: 'Mass',
  },
  {
    id: 3,
    code: 'kg_ish',
    dimensionId: 1,
    factorToBase: AlmostOne,
    offsetToBase: '0.0000000000',
    dimensionCode: 'Mass',
  },
];

interface Calls {
  readonly createCalls: Record<string, unknown>[];
  readonly convertCalls: Record<string, unknown>[];
}

function mockApi(): Calls {
  const createCalls: Record<string, unknown>[] = [];
  const convertCalls: Record<string, unknown>[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';

      const json = (body: unknown, status = 200): Response =>
        new Response(JSON.stringify(body), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      const body = (): Record<string, unknown> =>
        JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>;

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      }

      if (url.endsWith('/api/v1/me')) {
        return json({
          userId: 1,
          userName: 'bootstrap',
          language: 'en',
          permissions: ['Uom.EditCatalog'],
          isSimulation: false,
          denies: [],
          grants: {},
          mustChangePassword: false,
          simulatedForUserId: null,
        });
      }

      if (url.includes('/api/v1/units/convert') && method === 'POST') {
        convertCalls.push(body());
        return json({ unit: 'g', value: '1000.0000000000' });
      }

      if (url.includes('/api/v1/units') && method === 'GET') {
        return json(seededUnits);
      }

      if (url.includes('/api/v1/units') && method === 'POST') {
        createCalls.push(body());
        return json({ ...seededUnits[1], id: 9, code: String(body()['code']) });
      }

      throw new Error(`Немає мока для ${method} ${url}`);
    }),
  );

  return { createCalls, convertCalls };
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <UnitsPage />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function rowOf(table: HTMLElement, code: string): HTMLElement {
  const cell = within(table).getByText(code);
  const row = cell.closest('tr');

  expect(row, `рядка одиниці ${code} немає в таблиці`).not.toBeNull();
  return row as HTMLElement;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('UnitsPage: базова одиниця впізнається по ЗНАЧЕННЮ множника', () => {
  it('«1.0000000000» — це базова одиниця, «0.0010000000» — ні', async () => {
    mockApi();
    await loadCatalog('en', 'private');
    show();

    const table = await screen.findByRole('table');
    await within(table).findByText('kg');

    // ⛔ Мутаційний доказ: приберіть у `normalizeDecimal` обрізання хвостових
    // нулів (`fraction.replace(/0+$/, '')`) — і значок зникне, бо
    // `'1.0000000000' !== '1'` текстом.
    expect(within(rowOf(table, 'kg')).getByText('base')).toBeDefined();
    expect(within(rowOf(table, 'g')).queryByText('base')).toBeNull();
  });

  it('множник, що відходить від одиниці на 17-му знаку, базовим НЕ вважається', async () => {
    mockApi();
    await loadCatalog('en', 'private');
    show();

    const table = await screen.findByRole('table');
    await within(table).findByText('kg_ish');

    // ⛔ Саме цей випадок і ламає `Number(x) === 1`: перевірка через число
    // назвала б `kg_ish` базовою одиницею, і множник, на який усе множиться,
    // мовчки став би одиницею.
    expect(Number(AlmostOne) === 1).toBe(true);
    expect(within(rowOf(table, 'kg_ish')).queryByText('base')).toBeNull();
  });
});

describe('UnitsPage: десяткові йдуть на сервер рядком, яким їх ввели', () => {
  it('множник нової одиниці не проходить через число дорогою до запиту', async () => {
    const { createCalls } = mockApi();
    await loadCatalog('en', 'private');
    show();

    const table = await screen.findByRole('table');
    await within(table).findByText('kg');

    fireEvent.click(await screen.findByRole('button', { name: 'New unit' }));
    const dialog = await screen.findByRole('dialog');

    fireEvent.change(within(dialog).getByLabelText('Code'), { target: { value: 'lb' } });
    fireEvent.change(within(dialog).getByLabelText('Symbol'), { target: { value: 'lb' } });
    fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Pound' } });
    fireEvent.click(within(dialog).getByLabelText('Dimension'));
    fireEvent.click(await screen.findByRole('option', { name: 'Mass' }));

    // ⚠ 16 знаків після коми — ширина колонки `decimal(28,16)`, заради якої
    // контракт і перевели на рядок. У `double` таке число не вміщається: 19
    // значущих цифр проти ~17 доступних.
    fireEvent.change(within(dialog).getByLabelText('Factor to base'), {
      target: { value: ' 123.4567890123456789 ' },
    });

    fireEvent.click(within(dialog).getByRole('button', { name: 'New unit' }));

    await waitFor(() => {
      expect(createCalls).toHaveLength(1);
    });

    /*
     * ⛔ Мутаційний доказ: поверніть на шляху відправки `Number(...)`
     * (`factorToBase: String(Number(newFactor.trim()))`) — і на сервер піде
     * `'123.45678901234568'`, тобто два останні знаки множника зникнуть ще в
     * клієнті й мовчки. Літерал тут саме РЯДКОМ: очікування у вигляді числа
     * (`toBe(123.4567890123456789)`) лишилося б зеленим після тієї ж мутації,
     * бо обидва боки порівняння пройшли б через ту саму втрату.
     */
    expect(createCalls[0]?.['factorToBase']).toBe('123.4567890123456789');
    expect(createCalls[0]?.['offsetToBase']).toBe('0');
  });

  it('значення конверсії їде рядком, а «не число» кнопку не випускає', async () => {
    const { convertCalls } = mockApi();
    await loadCatalog('en', 'private');
    show();

    const table = await screen.findByRole('table');
    await within(table).findByText('kg');

    fireEvent.change(screen.getByLabelText('Value'), {
      target: { value: '2.5000000000000001' },
    });

    fireEvent.click(screen.getByLabelText('From'));
    fireEvent.click(await screen.findByRole('option', { name: 'kg' }));
    fireEvent.click(screen.getByLabelText('To'));
    fireEvent.click(await screen.findByRole('option', { name: 'g' }));

    const convertButton = screen.getByRole('button', { name: 'Convert' });
    fireEvent.click(convertButton);

    await waitFor(() => {
      expect(convertCalls).toHaveLength(1);
    });

    expect(convertCalls[0]?.['value']).toBe('2.5000000000000001');

    // ⚠ Поле тепер текстове, тож «не число» стало можливим станом: кнопка,
    // яка веде у відому відмову сервера, гірша за вимкнену.
    fireEvent.change(screen.getByLabelText('Value'), { target: { value: 'дві з половиною' } });
    expect(screen.getByRole('button', { name: 'Convert' })).toHaveProperty('disabled', true);
  });
});
