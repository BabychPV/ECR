import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { UnitsPage } from '../UnitsPage';
import { testTheme } from '@/test/render';

/**
 * `X-36`: результат конвертора одиниць (`/admin/units`) друкував рядок
 * `decimal(28,16)`, яким його віддав сервер, — прямо, без жодного форматування.
 * Хвостові нулі («0.4535923700000000») і відсутній розділювач тисяч
 * («1234567.45359237» замість «1,234,567.45359237») розходились із рештою
 * продукту: колонки «Factor to base»/«Offset to base» ЦІЄЇ Ж сторінки й
 * `SnapshotRowsModal` показують те саме подання лише через канонічний
 * `formatDecimal` (`shared/format/number.ts`).
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
};

const seededUnits = [
  { id: 1, code: 'kg', dimensionId: 1, factorToBase: '1.0000000000', offsetToBase: '0.0000000000', dimensionCode: 'Mass' },
  { id: 2, code: 'g', dimensionId: 1, factorToBase: '0.0010000000', offsetToBase: '0.0000000000', dimensionCode: 'Mass' },
];

/** Хвостові нулі й достатньо цифр у цілій частині, щоб потребувати розділювач тисяч. */
const RawConvertedValue = '1234567.4535923700000000';
const ExpectedShown = '1,234,567.45359237';

function mockApi(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';

      const json = (body: unknown, status = 200): Response =>
        new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

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

      if (url.includes('/api/v1/units/convert') && method === 'POST') {
        return json({ unit: 'g', value: RawConvertedValue });
      }

      if (url.includes('/api/v1/units') && method === 'GET') {
        return json(seededUnits);
      }

      throw new Error(`Немає мока для ${method} ${url}`);
    }),
  );
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

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('UnitsPage: результат конверсії показаний через канонічне форматування (X-36)', () => {
  it('хвостові нулі зникають, тисячі розділені комою — як і скрізь у продукті', async () => {
    mockApi();
    await loadCatalog('en', 'private');
    show();

    const table = await screen.findByRole('table');
    await within(table).findByText('kg');

    fireEvent.change(screen.getByLabelText('Value'), { target: { value: '1000' } });

    fireEvent.click(screen.getByLabelText('From'));
    fireEvent.click(await screen.findByRole('option', { name: 'kg' }));
    fireEvent.click(screen.getByLabelText('To'));
    fireEvent.click(await screen.findByRole('option', { name: 'g' }));

    fireEvent.click(screen.getByRole('button', { name: 'Convert' }));

    // ⛔ Мутаційний доказ: поверніть `{result.value} {result.unit}` без
    // `formatDecimal` — цей `findByText` не знайде нічого, бо на екрані
    // лишиться сирий `1234567.4535923700000000 g`.
    const shown = await waitFor(() => screen.getByText(`${ExpectedShown} g`));
    expect(shown).toBeDefined();

    // Сирий рядок сервера на екрані більше не з'являється взагалі.
    expect(screen.queryByText(new RegExp(RawConvertedValue.replace('.', '\\.')))).toBeNull();
  });
});
