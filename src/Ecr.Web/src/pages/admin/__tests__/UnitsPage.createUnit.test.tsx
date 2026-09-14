import type { JSX } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { UnitsPage } from '../UnitsPage';

/**
 * UI-аудит, lane 4: «Units page is entirely read-only — no way to create,
 * edit, or delete a unit of measure»
 * (`docs/build/audit-drafts/lane4-units-page-no-create-control.md`, severity
 * High): жоден обліковий запис, включно з повноправним адміністратором, не
 * мав шляху додати одиницю виміру — той самий клас дефекту, що вже
 * виправлений для довідників (`Q-200`).
 *
 * ⚠ `Select` (`@mantine/core`) зависає під jsdom (`Q-299`) — заглушено
 * стандартним стабом.
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

const seededUnits = [
  { id: 1, code: 'kg', dimensionId: 1, factorToBase: 1, offsetToBase: 0, dimensionCode: 'Mass' },
  { id: 2, code: 'm3', dimensionId: 2, factorToBase: 1, offsetToBase: 0, dimensionCode: 'Volume' },
];

function mockApi(options: { permissions: string[] }): { createCalls: unknown[] } {
  const createCalls: unknown[] = [];
  let units = seededUnits;

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

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      }

      if (url.endsWith('/api/v1/me')) {
        return json({
          userId: 1,
          userName: 'bootstrap',
          language: 'en',
          permissions: options.permissions,
          isSimulation: false,
          denies: [],
          grants: {},
          mustChangePassword: false,
          simulatedForUserId: null,
        });
      }

      if (url.includes('/api/v1/units') && method === 'GET') {
        return json(units);
      }

      if (url.includes('/api/v1/units') && method === 'POST') {
        const body = JSON.parse(String(init?.body ?? '{}')) as { code: string };
        createCalls.push(body);

        const created = {
          id: 3,
          code: body.code,
          dimensionId: 1,
          factorToBase: 0.001,
          offsetToBase: 0,
          dimensionCode: 'Mass',
        };
        units = [...units, created];

        return json(created);
      }

      throw new Error(`Немає мока для ${method} ${url}`);
    }),
  );

  return { createCalls };
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <UnitsPage />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('UnitsPage: заведення нової одиниці виміру (lane4)', () => {
  it('з правом Uom.EditCatalog нова одиниця з\'являється в списку після заведення', async () => {
    const { createCalls } = mockApi({ permissions: ['Uom.EditCatalog'] });
    await loadCatalog('en', 'private');
    show();

    const table = await screen.findByRole('table');
    await within(table).findByText('kg');

    fireEvent.click(await screen.findByRole('button', { name: 'New unit' }));

    const dialog = await screen.findByRole('dialog');

    fireEvent.change(within(dialog).getByLabelText('Code'), { target: { value: 'g' } });
    fireEvent.change(within(dialog).getByLabelText('Symbol'), { target: { value: 'g' } });
    fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Gram' } });
    fireEvent.change(within(dialog).getByLabelText('Dimension'), { target: { value: '1' } });

    fireEvent.click(within(dialog).getByRole('button', { name: 'New unit' }));

    await waitFor(() => {
      expect(createCalls).toHaveLength(1);
    });

    expect(createCalls[0]).toMatchObject({
      code: 'g',
      symbolL10n: { en: 'g' },
      nameL10n: { en: 'Gram' },
      dimensionId: 1,
    });

    // ⛔ Мутаційний доказ (RED на невиправленому коді до цієї картки): до
    // фіксу форми заведення не існувало взагалі — цей запит не знайшов би
    // ані кнопки «New unit», ані щойно заведеної одиниці в списку.
    await waitFor(() => {
      expect(within(table).getByText('g')).toBeDefined();
    });
  });

  it('без права Uom.EditCatalog кнопки «New unit» немає', async () => {
    mockApi({ permissions: [] });
    await loadCatalog('en', 'private');
    show();

    const table = await screen.findByRole('table');
    await within(table).findByText('kg');
    expect(screen.queryByRole('button', { name: 'New unit' })).toBeNull();
  });
});
