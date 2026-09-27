import { type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cancelAutosave, useDocumentPending } from '../autosave';
import { pendingSlice, resetPending, subscribePending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * Протягування маркером заповнення (fill handle) зберігається, а не лише
 * малюється.
 *
 * ⛔ Що ламалося. На протягування RevoGrid шле `afteredit` у ДІАПАЗОННІЙ формі —
 * `onRangeEdit` у `revo-grid.entry.js` емітить `{ data, models, type }`, де
 * `data` — `{ [індекс рядка]: { [prop колонки]: значення } }` (будує
 * `ColumnService.getRangeData`), `models` — `{ [індекс рядка]: модель рядка }`
 * (`collectModelsOfRange`), і НЕМАЄ ні `prop`, ні `model`, ні `val`. Обробник
 * читав лише одиночну форму, отримував `columnCode = ''`, і `captureEdit`
 * мовчки відмовляв. Сітка показувала протягнуті значення, у сховище правок і в
 * PATCH не йшло нічого — тиха втрата даних до першого перевідкриття.
 *
 * ⚠ Заглушка шле рівно ту форму, яку шле бібліотека: ключі `data`/`models` —
 * рядкові індекси рядків (ключі об'єкта), значення — сирі значення моделі
 * рядка-джерела (для `Int` — число).
 */

type RangeData = Record<string, Record<string, unknown>>;

/** Протягування, які «робить» заглушка: що скопійовано і в які рядки. */
const Fills: { id: string; data: RangeData; rows: Record<string, string> }[] = [
  // Блок 2×2 з рядка-джерела r3 (C1=5, C2=6) вгору на r1, r2.
  { id: 'block', data: { 0: { C1: 5, C2: 6 }, 1: { C1: 5, C2: 6 } }, rows: { 0: 'r1', 1: 'r2' } },
  // Вниз на r2 і r3 через C1 і read-only C3; r3:C1 — без права (`NoGrant`).
  { id: 'guarded', data: { 1: { C1: 9, C3: 9 }, 2: { C1: 9, C3: 9 } }, rows: { 1: 'r2', 2: 'r3' } },
];

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { onAfteredit?: (event: { detail: unknown }) => void }) => (
    <div data-testid="revogrid-stub">
      {Fills.map((fill) => (
        <button
          key={fill.id}
          type="button"
          onClick={() =>
            props.onAfteredit?.({
              detail: {
                data: fill.data,
                models: Object.fromEntries(
                  Object.entries(fill.rows).map(([index, rowKey]) => [index, { __rowKey: rowKey }]),
                ),
                type: 'rgRow',
              },
            })
          }
        >
          {`fill-${fill.id}`}
        </button>
      ))}
    </div>
  ),
}));

const DocumentId = 11;
const Table = 3;
const Period = 202609;

function column(code: string, ordinal: number, isReadOnly = false): ColumnDto {
  return {
    code,
    dataType: 'Int',
    defaultValue: null,
    displayFormat: null,
    header: code,
    id: ordinal + 1,
    isReadOnly,
    isRequired: false,
    isRequiredByMethodology: false,
    lookupRegistryDefId: null,
    ordinal,
    unitId: null,
    unitSymbol: null,
  };
}

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: { 'r3:C1': 'NoGrant' },
    periodKey: Period,
    tableInstanceId: Table,
    columns: [column('C1', 0), column('C2', 1), column('C3', 2, true)],
    rows: ['r1', 'r2', 'r3'].map((rowKey, ordinal) => ({
      cells: rowKey === 'r3' ? { C1: 5, C2: 6, C3: 0 } : { C1: 1, C2: 2, C3: 0 },
      isOrphaned: false,
      label: null,
      ordinal,
      rowKey,
      rowKind: 'Item',
      rowVersion: 'v1',
    })),
  };
}

interface SentCell {
  rowKey: string;
  columnCode: string;
  value: unknown;
}

const patches: SentCell[][] = [];

function mockServer(): void {
  patches.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        const request = JSON.parse(String(init.body)) as {
          rows: { rowKey: string; cells: { columnCode: string; value: unknown }[] }[];
        };
        const cells = request.rows.flatMap((row) =>
          row.cells.map((cell) => ({ rowKey: row.rowKey, columnCode: cell.columnCode, value: cell.value })),
        );
        patches.push(cells);

        return new Response(
          JSON.stringify({
            appliedCells: cells.length,
            rowVersions: Object.fromEntries(cells.map((cell) => [cell.rowKey, 'v2'])),
            validation: [],
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      return new Response(JSON.stringify(sliceFixture()), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

function DocumentHost(): JSX.Element {
  useDocumentPending(DocumentId);

  return (
    <DocumentGrid
      documentId={DocumentId}
      tableInstanceId={Table}
      periodKey={Period}
      readOnly={false}
      allowsDynamicRows={false}
      maxDynamicRows={null}
    />
  );
}

async function show(): Promise<HTMLElement> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentHost />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return screen.findByTestId('revogrid-stub');
}

async function wait(ms: number): Promise<void> {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, ms));
  });
}

const sorted = (cells: readonly SentCell[] | undefined): SentCell[] =>
  [...(cells ?? [])].sort((a, b) => `${a.rowKey}:${a.columnCode}`.localeCompare(`${b.rowKey}:${b.columnCode}`));

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('протягування маркером заповнення', () => {
  it('діапазонна форма afteredit доходить до сховища правок і до PATCH', async () => {
    mockServer();
    await show();

    fireEvent.click(screen.getByRole('button', { name: 'fill-block' }));

    // ⛔ До фіксу тут було порожньо: сітка показувала значення, сховище — ні.
    expect([...pendingSlice(Table, Period).keys()].sort()).toEqual(['r1:C1', 'r1:C2', 'r2:C1', 'r2:C2']);

    await wait(300);

    expect(patches).toHaveLength(1);
    expect(sorted(patches[0])).toEqual([
      { rowKey: 'r1', columnCode: 'C1', value: 5 },
      { rowKey: 'r1', columnCode: 'C2', value: 6 },
      { rowKey: 'r2', columnCode: 'C1', value: 5 },
      { rowKey: 'r2', columnCode: 'C2', value: 6 },
    ]);
  });

  it('read-only колонка й комірка без права в PATCH не потрапляють', async () => {
    mockServer();
    await show();

    fireEvent.click(screen.getByRole('button', { name: 'fill-guarded' }));
    await wait(300);

    // ⚠ Лише r2:C1: C3 — read-only колонка, r3:C1 — `NoGrant`. Бібліотека сама
    // фільтрує read-only (`isReadOnly` у `getRangeData`), але права могли
    // змінитися між рендером і протягуванням — перевірка тут та сама, що в
    // ручного введення й вставки (`decide`).
    expect(patches).toEqual([[{ rowKey: 'r2', columnCode: 'C1', value: 9 }]]);
    expect([...pendingSlice(Table, Period).keys()]).toEqual([]);
  });

  it('діапазон лягає в сховище ОДНИМ записом, а не комірка за коміркою', async () => {
    mockServer();
    await show();

    let notified = 0;
    const unsubscribe = subscribePending(() => {
      notified += 1;
    });
    fireEvent.click(screen.getByRole('button', { name: 'fill-block' }));
    unsubscribe();

    expect(pendingSlice(Table, Period).size).toBe(4);
    expect(notified).toBe(1);
    await wait(300);
  });

  it('Ctrl+Z відкочує протягування одним кроком', async () => {
    mockServer();
    const grid = await show();

    fireEvent.click(screen.getByRole('button', { name: 'fill-block' }));
    await wait(300);
    expect(patches).toHaveLength(1);

    fireEvent.keyDown(grid, { key: 'z', ctrlKey: true });
    await wait(300);

    expect(patches).toHaveLength(2);
    expect(sorted(patches[1])).toEqual([
      { rowKey: 'r1', columnCode: 'C1', value: 1 },
      { rowKey: 'r1', columnCode: 'C2', value: 2 },
      { rowKey: 'r2', columnCode: 'C1', value: 1 },
      { rowKey: 'r2', columnCode: 'C2', value: 2 },
    ]);
  });
});
