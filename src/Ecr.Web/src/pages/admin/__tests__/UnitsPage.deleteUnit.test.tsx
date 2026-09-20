import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { UnitsPage } from '../UnitsPage';
import { testTheme } from '@/test/render';

/**
 * Директива №15, BE-15: діалог видалення одиниці СПЕРШУ показує залежних, а
 * на відмову `409 ECR-UOM-0409` — перелік із самої відмови замість «повторити».
 */

const SeededStrings: Record<string, string> = {
  'units.title': 'Units of measure',
  'units.code': 'Unit',
  'units.deleted': 'Unit removed.',
  'units.deleteUnused': 'Nothing refers to this unit.',
  'units.deleteUsedIn': 'Referenced in {total} place(s):',
  'common.cancel': 'Cancel',
  'common.delete': 'Remove',
};

const column = { kind: 'templateColumn', id: '7', label: 'Fuel.Mass', route: '/admin/templates/1/versions/2' };

interface Api {
  deleteCalls: string[];
}

function mockApi(options: {
  permissions: string[];
  usage: { total: number; items: unknown[] };
  deleteStatus: 204 | 409;
}): Api {
  const deleteCalls: string[] = [];
  // ⚠ Множники рядками: `decimal` у відповідях їде рядком (`e470777a`).
  let units = [
    {
      id: 1,
      code: 'kg',
      dimensionId: 1,
      factorToBase: '1.0000000000',
      offsetToBase: '0.0000000000',
      dimensionCode: 'Mass',
    },
    {
      id: 5,
      code: 'lb',
      dimensionId: 1,
      factorToBase: '0.4535923700',
      offsetToBase: '0.0000000000',
      dimensionCode: 'Mass',
    },
  ];

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
          permissions: options.permissions,
          isSimulation: false,
          denies: [],
          grants: {},
          mustChangePassword: false,
          simulatedForUserId: null,
        });
      }

      if (url.endsWith('/api/v1/units/5/usage')) {
        return json(options.usage);
      }

      if (url.endsWith('/api/v1/units/5') && method === 'DELETE') {
        deleteCalls.push(url);

        if (options.deleteStatus === 409) {
          return json(
            {
              status: 409,
              title: 'Unit is in use',
              errorCode: 'ECR-UOM-0409',
              detail: 'Unit "lb" cannot be removed.',
              total: '1',
              references: [column],
            },
            409,
            'application/problem+json',
          );
        }

        units = units.filter((unit) => unit.id !== 5);

        return new Response(null, { status: 204 });
      }

      if (url.endsWith('/api/v1/units')) {
        return json(units);
      }

      throw new Error(`No mock for ${method} ${url}`);
    }),
  );

  return { deleteCalls };
}

async function openDialog(): Promise<HTMLElement> {
  await loadCatalog('en', 'private');

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <UnitsPage />
      </QueryClientProvider>
    </MantineProvider>,
  );

  fireEvent.click(await screen.findByRole('button', { name: 'Remove lb' }));

  return screen.findByRole('dialog');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('UnitsPage: видалення одиниці (BE-15)', () => {
  it('одиниця з залежними: діалог показує їх і не пропонує видалити', async () => {
    const api = mockApi({
      permissions: ['Uom.EditCatalog'],
      usage: { total: 23, items: [column] },
      deleteStatus: 204,
    });

    const dialog = await openDialog();

    expect(await within(dialog).findByText('Referenced in 23 place(s):')).toBeTruthy();
    expect(within(dialog).getByText('Fuel.Mass')).toBeTruthy();

    // ⛔ Кнопки видалення немає: вона вела б у відому відмову.
    expect(within(dialog).queryByRole('button', { name: 'Remove' })).toBeNull();
    expect(api.deleteCalls).toHaveLength(0);
  });

  it('одиниця без залежних видаляється і зникає з переліку', async () => {
    const api = mockApi({
      permissions: ['Uom.EditCatalog'],
      usage: { total: 0, items: [] },
      deleteStatus: 204,
    });

    const dialog = await openDialog();

    await within(dialog).findByText('Nothing refers to this unit.');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Remove' }));

    await waitFor(() => {
      expect(api.deleteCalls).toHaveLength(1);
    });

    await waitFor(() => {
      expect(screen.queryByRole('button', { name: 'Remove lb' })).toBeNull();
    });
  });

  it('відмова 409 показує залежних із самої відмови замість «повторити»', async () => {
    // Посилання з'явилось, поки діалог стояв відкритий: перелік казав «0».
    mockApi({
      permissions: ['Uom.EditCatalog'],
      usage: { total: 0, items: [] },
      deleteStatus: 409,
    });

    const dialog = await openDialog();

    await within(dialog).findByText('Nothing refers to this unit.');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Remove' }));

    expect(await within(dialog).findByText('Fuel.Mass')).toBeTruthy();
    expect(within(dialog).getByText('Referenced in 1 place(s):')).toBeTruthy();
    expect(within(dialog).queryByRole('button', { name: 'Remove' })).toBeNull();
    expect(within(dialog).queryByRole('button', { name: /retry/i })).toBeNull();
  });

  it('без права Uom.EditCatalog кнопки видалення немає', async () => {
    mockApi({ permissions: ['Calculation.View'], usage: { total: 0, items: [] }, deleteStatus: 204 });
    await loadCatalog('en', 'private');

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <UnitsPage />
        </QueryClientProvider>
      </MantineProvider>,
    );

    await within(await screen.findByRole('table')).findByText('lb');

    expect(screen.queryByRole('button', { name: 'Remove lb' })).toBeNull();
  });
});
