import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnRegular } from '@revolist/revogrid';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { DefaultLanguage, setLanguage } from '@/shared/i18n';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * Формат комірки за `scale` колонки: `1.5` при `scale = 4` ПОКАЗУЄТЬСЯ як
 * `1.5000` (як формат комірки в Excel), але редактор і тіло PATCH бачать `1.5`.
 *
 * ⚠ Заглушка RevoGrid малює показ через продуктовий `cellTemplate`, а поруч —
 * сире значення моделі (`data-model`): саме його RevoGrid віддає редактору
 * комірки. Тож «на екрані» і «в редакторі» — два різні джерела, як у бібліотеці.
 */
vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: {
    columns?: ColumnRegular[];
    source?: Record<string, unknown>[];
    onAfteredit?: (event: { detail: unknown }) => void;
  }) => (
    <div data-testid="revogrid-stub">
      {(props.source ?? []).map((model) =>
        (props.columns ?? []).map((column) => {
          const prop = String(column.prop);
          const value = model[prop];

          return (
            <span
              key={`${String(model.__rowKey)}:${prop}`}
              data-testid={`cell-${String(model.__rowKey)}-${prop}`}
              data-model={value === null || value === undefined ? '' : String(value)}
            >
              {typeof column.cellTemplate === 'function'
                ? String(
                    column.cellTemplate(
                      (() => null) as never,
                      { value, model, prop } as never,
                      undefined as never,
                    ),
                  )
                : String(value ?? '')}
            </span>
          );
        }),
      )}
      <button
        type="button"
        onClick={() =>
          props.onAfteredit?.({ detail: { prop: 'C1', model: { __rowKey: 'r1' }, val: '1.5' } })
        }
      >
        edit-r1-C1
      </button>
    </div>
  ),
}));

function column(code: string, ordinal: number, scale: number | null): ColumnDto {
  return {
    code,
    dataType: 'Decimal',
    defaultValue: null,
    displayFormat: null,
    header: code,
    id: ordinal + 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    lookupRegistryDefId: null,
    ordinal,
    scale,
    unitId: null,
    unitSymbol: null,
  };
}

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    // C1 — масштаб 4; C2 — без масштабу (дзеркало: доповнення немає).
    // C3 — `Formula` зі scale 2: показ округлюється, модель — ні.
    columns: [
      column('C1', 0, 4),
      column('C2', 1, null),
      { ...column('C3', 2, 2), dataType: 'Formula', isReadOnly: true },
    ],
    rows: [
      {
        cells: { C1: '2.0000000000', C2: '1.5000000000', C3: '1.0050000000' },
        isOrphaned: false,
        label: null,
        ordinal: 0,
        rowKey: 'r1',
        rowVersion: 'v1',
        rowKind: 'Item',
      },
      {
        cells: { C1: null, C2: null },
        isOrphaned: false,
        label: null,
        ordinal: 1,
        rowKey: 'r2',
        rowVersion: 'v2',
        rowKind: 'Item',
      },
    ],
  };
}

const patched: { rowKey: string; cells: { columnCode: string; value: unknown }[] }[] = [];

function mockServer(): void {
  patched.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn((_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        const body = JSON.parse(String(init.body)) as {
          rows: { rowKey: string; cells: { columnCode: string; value: unknown }[] }[];
        };
        patched.push(...body.rows);

        return Promise.resolve(
          new Response(JSON.stringify({ appliedCells: 1, rowVersions: {}, validation: [] }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
        );
      }

      return Promise.resolve(
        new Response(JSON.stringify(sliceFixture()), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentGrid
          documentId={1}
          tableInstanceId={1}
          periodKey={202609}
          readOnly={false}
          allowsDynamicRows={false}
          maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

beforeEach(() => {
  setLanguage('en');
});

afterEach(() => {
  cancelAutosave();
  resetPending();
  setLanguage(DefaultLanguage);
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('формат комірки: scale = 4', () => {
  it('ввід «1.5» — на екрані «1.5000», у редакторі й у PATCH — «1.5»', async () => {
    mockServer();
    show();

    await screen.findByTestId('revogrid-stub');
    fireEvent.click(screen.getByRole('button', { name: 'edit-r1-C1' }));

    await waitFor(() => expect(screen.getByTestId('cell-r1-C1').textContent).toBe('1.5000'));

    // Редактор RevoGrid відкривається із сирого значення моделі — без нулів.
    expect(screen.getByTestId('cell-r1-C1').getAttribute('data-model')).toBe('1.5');

    await waitFor(() => expect(patched).toHaveLength(1), { timeout: 5000 });
    // ⛔ Нулі показу на дріт не йдуть.
    expect(patched[0]?.cells[0]?.value).toBe('1.5');
  });

  it('серверне «2.0000000000» показується як «2.0000»', async () => {
    mockServer();
    show();

    expect((await screen.findByTestId('cell-r1-C1')).textContent).toBe('2.0000');
  });

  it('порожня комірка — порожньо, не «0.0000»', async () => {
    mockServer();
    show();

    expect((await screen.findByTestId('cell-r2-C1')).textContent).toBe('');
  });

  it('Formula, scale 2: «1.005» показано повністю, а не обрізано до «1.01»', async () => {
    /*
     * ✒ 2026-09-23 (`U-05`): `scale` — НИЖНЯ межа подачі
     * (доповнення нулями), а не верхня. Сервер звіряє масштаб
     * лише для `Decimal` (`ColumnDef.Validate` п. 7), тож вихід
     * `Formula`/`Calculated` законно несе довший дріб — і це значущі
     * знаки даних про викиди, а не шум.
     */
    mockServer();
    show();

    const cell = await screen.findByTestId('cell-r1-C3');

    expect(cell.textContent).toBe('1.005');
    // Модель (редактор, буфер, PATCH) — як і доті, сире значення сервера.
    expect(cell.getAttribute('data-model')).toBe('1.0050000000');
  });

  it('дзеркало: колонка без scale нулями не доповнюється', async () => {
    mockServer();
    show();

    expect((await screen.findByTestId('cell-r1-C2')).textContent).toBe('1.5');
  });
});
