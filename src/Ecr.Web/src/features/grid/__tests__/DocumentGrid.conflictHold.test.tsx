import { type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnRegular } from '@revolist/revogrid';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cancelAutosave, useDocumentPending } from '../autosave';
import { pendingSlice, resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * Конфлікт версії рядка (`409`) більше не блокує наступні пакети.
 *
 * ⛔ Правка, що отримала `409`, не утримувалась: автозбереження везло її з
 * кожним наступним пакетом, сервер відхиляв його цілком — той самий клас, що
 * `V-01`, лише з іншим кодом. Тепер конфліктна правка тримається, доки людина
 * її не розв'яже (Retry або нова правка комірки). Перелік розбіжностей
 * (`grid.conflictTitle`) показується як і раніше.
 */

type CellProps = { class?: string; title?: string; 'data-cell-state'?: string };

const Cells = [
  { id: 'bad', rowKey: 'r1', column: 'C1', val: 'abc' },
  { id: 'fixed', rowKey: 'r1', column: 'C1', val: '42' },
  { id: 'good', rowKey: 'r1', column: 'C2', val: '7' },
  { id: 'other-row', rowKey: 'r2', column: 'C2', val: '8' },
  { id: 'revert', rowKey: 'r1', column: 'C1', val: '1' },
] as const;

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: {
    columns?: ColumnRegular[];
    source?: Record<string, unknown>[];
    onAfteredit?: (event: { detail: unknown }) => void;
  }) => {
    const mirror = (rowKey: string, code: string): CellProps => {
      const column = (props.columns ?? []).find((candidate) => candidate.prop === code);
      const cellProperties = column?.cellProperties as ((args: { model: unknown }) => CellProps) | undefined;

      return cellProperties?.({ model: { __rowKey: rowKey } }) ?? {};
    };

    return (
      <div data-testid="revogrid-stub">
        {Cells.map((cell) => (
          <button
            key={cell.id}
            type="button"
            onClick={() =>
              props.onAfteredit?.({
                detail: { prop: cell.column, model: { __rowKey: cell.rowKey }, val: cell.val },
              })
            }
          >
            {`edit-${cell.id}`}
          </button>
        ))}
        <span data-testid="shown-r1:C1">
          {String((props.source ?? []).find((row) => row['__rowKey'] === 'r1')?.['C1'] ?? '')}
        </span>
        {['r1:C1', 'r1:C2', 'r2:C2'].map((key) => {
          const [rowKey, code] = key.split(':') as [string, string];
          const properties = mirror(rowKey, code);

          return (
            <span key={key} data-testid={`cell-${key}`} data-class={properties.class ?? ''} title={properties.title ?? ''}>
              {properties['data-cell-state'] ?? 'none'}
            </span>
          );
        })}
      </div>
    );
  },
}));

const DocumentId = 9;
const Table = 1;
const Period = 202609;
const Refusal = 'Комірку вже змінив інший користувач.';

/** Людина розв'язала конфлікт — сервер більше не відхиляє `r1:C1`. */
let allowC1 = false;

function column(code: string, ordinal: number): ColumnDto {
  return {
    code,
    dataType: 'Int',
    defaultValue: null,
    displayFormat: null,
    header: code,
    id: ordinal + 1,
    isReadOnly: false,
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
    cellPermissions: {},
    periodKey: Period,
    tableInstanceId: Table,
    columns: [column('C1', 0), column('C2', 1)],
    rows: ['r1', 'r2'].map((rowKey, ordinal) => ({
      cells: { C1: 1, C2: 2 },
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

/** Кожен PATCH: що в ньому було і що відповів «сервер». */
const patches: { cells: SentCell[]; status: number }[] = [];

function cellsOf(body: string): SentCell[] {
  const request = JSON.parse(body) as {
    rows: { rowKey: string; cells: { columnCode: string; value: unknown }[] }[];
  };

  return request.rows.flatMap((row) =>
    row.cells.map((cell) => ({ rowKey: row.rowKey, columnCode: cell.columnCode, value: cell.value })),
  );
}

function mockServer(): void {
  patches.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        const cells = cellsOf(String(init.body));

        // ⛔ Конфлікт версії: чужа правка вже змінила `r1:C1` — пакет, що несе
        // цю комірку, відхиляється ЦІЛКОМ (`ECR-CELL-0409`) з переліком розбіжностей.
        if (cells.some((cell) => cell.rowKey === 'r1' && cell.columnCode === 'C1') && !allowC1) {
          patches.push({ cells, status: 409 });

          return new Response(
            JSON.stringify({
              title: 'Конфлікт',
              status: 409,
              detail: Refusal,
              errorCode: 'ECR-CELL-0409',
              correlationId: 'c1',
              conflicts: [
                { rowKey: 'r1', columnCode: 'C1', theirValue: 99, theirUser: 'A. Serikbayev', theirChangedAt: null },
              ],
            }),
            { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
          );
        }

        patches.push({ cells, status: 200 });
        const rowVersions = Object.fromEntries(cells.map((cell) => [cell.rowKey, 'v2']));

        return new Response(JSON.stringify({ appliedCells: cells.length, rowVersions, validation: [] }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
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

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentHost />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function wait(ms: number): Promise<void> {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, ms));
  });
}

/** Дебаунс 500 мс + оберт до мок-сервера. */
const passDebounce = (): Promise<void> => wait(900);


afterEach(() => {
  allowC1 = false;
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('409: конфліктна правка не їде з наступними пакетами', () => {
  it('конфлікт r1:C1 → нова правка іншої комірки йде БЕЗ неї і сервер її приймає', async () => {
    mockServer();
    show();
    await screen.findByTestId('revogrid-stub');

    fireEvent.click(screen.getByRole('button', { name: 'edit-fixed' }));
    await passDebounce();

    expect(patches[0]?.status).toBe(409);

    // Поведінка конфлікту для людини — та сама: перелік розбіжностей.
    expect(screen.getByText(/grid.conflictTitle/)).toBeTruthy();
    expect(screen.getByTestId('cell-r1:C1').dataset['class']).toContain('ecr-cell-save-error');

    fireEvent.click(screen.getByRole('button', { name: 'edit-good' }));
    await passDebounce();

    expect(patches).toHaveLength(2);
    expect(patches[1]?.cells).toEqual([{ rowKey: 'r1', columnCode: 'C2', value: 7 }]);
    expect(patches[1]?.status).toBe(200);
    expect([...pendingSlice(Table, Period).keys()]).toEqual(['r1:C1']);
  });

  it('правку, відхилену разом із конфліктною, довозить окремий пакет', async () => {
    mockServer();
    show();
    await screen.findByTestId('revogrid-stub');

    fireEvent.click(screen.getByRole('button', { name: 'edit-fixed' }));
    fireEvent.click(screen.getByRole('button', { name: 'edit-other-row' }));
    await passDebounce();
    await passDebounce();

    expect(patches[0]?.status).toBe(409);
    expect(patches[1]?.cells).toEqual([{ rowKey: 'r2', columnCode: 'C2', value: 8 }]);
    expect(patches[1]?.status).toBe(200);
  });

  it("розв'язаний конфлікт повторюється за явною дією — Retry", async () => {
    mockServer();
    show();
    await screen.findByTestId('revogrid-stub');

    fireEvent.click(screen.getByRole('button', { name: 'edit-fixed' }));
    await passDebounce();

    allowC1 = true;
    fireEvent.click(await screen.findByRole('button', { name: /grid\.retrySave/ }));
    await wait(300);

    expect(patches[1]?.cells).toEqual([{ rowKey: 'r1', columnCode: 'C1', value: 42 }]);
    expect(patches[1]?.status).toBe(200);
    expect(pendingSlice(Table, Period).size).toBe(0);
  });
});
