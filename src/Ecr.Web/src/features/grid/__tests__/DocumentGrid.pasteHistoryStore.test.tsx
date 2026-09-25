import { type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnRegular } from '@revolist/revogrid';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cancelAutosave, useDocumentPending } from '../autosave';
import { pendingSlice, resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * Вставка й Undo/Redo — тим самим шляхом, що й звичайна правка (продовження `V-01`).
 *
 * ⛔ Доти обидва шляхи будували патч ПОВЗ сховище правок: відхилена вставка
 * показувала причину, але без маркера комірки й «Retry save», і зникала з
 * перезавантаженням; невдалий undo зникав так само. Тепер правки спершу
 * лягають у сховище: успіх їх знімає, відмова — тримає (маркер, Retry, не
 * блокує інших), минущий збій — лишає незбереженими для повтору.
 *
 * ⚠ Мок-сервер той самий, що в `DocumentGrid.rejectedCellBlocking.test.tsx`:
 * нечислове в `C1` — відмова пакета цілком.
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
const Refusal = 'Колонка «C1» очікує число.';

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

/** Примусові відповіді наступним PATCH (5xx — минущий збій). */
const forced: number[] = [];

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

        const failure = forced.shift();
        if (failure !== undefined) {
          patches.push({ cells, status: failure });

          return new Response(
            JSON.stringify({ title: 'збій', status: failure, detail: 'Сервер недоступний.', errorCode: 'ECR-SYS-0500', correlationId: 'c2' }),
            { status: failure, headers: { 'Content-Type': 'application/problem+json' } },
          );
        }

        // ⛔ Як справжній `CellValueReader`: нечислове в `Int` — відмова ВСЬОГО
        // пакета, без часткового застосування.
        const bad = cells.find((cell) => cell.columnCode === 'C1' && typeof cell.value !== 'number');
        if (bad !== undefined) {
          patches.push({ cells, status: 422 });

          return new Response(
            JSON.stringify({
              title: 'Помилка валідації',
              status: 422,
              detail: Refusal,
              errorCode: 'ECR-CELL-0422',
              correlationId: 'c1',
              columnCode: 'C1',
            }),
            { status: 422, headers: { 'Content-Type': 'application/problem+json' } },
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
  forced.length = 0;
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

function paste(text: string): void {
  // ⚠ Якір вставки без виділення — кут таблиці, тобто `r1:C1`.
  fireEvent.paste(screen.getByTestId('revogrid-stub'), {
    clipboardData: { getData: () => text },
  });
}

describe('вставка й Undo/Redo зберігаються через сховище правок', () => {
  it('відхилена вставка лишається незбереженою: маркер, причина, Retry — і не блокує інших', async () => {
    mockServer();
    show();
    await screen.findByTestId('revogrid-stub');

    paste('abc');
    await wait(300);

    expect(patches).toHaveLength(1);
    expect(patches[0]?.status).toBe(422);
    expect([...pendingSlice(Table, Period).keys()]).toEqual(['r1:C1']);
    await waitFor(() => expect(screen.getByTestId('cell-r1:C1').dataset['class']).toContain('ecr-cell-save-error'));
    expect(screen.getByRole('button', { name: /grid\.retrySave/ })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'edit-good' }));
    await passDebounce();

    expect(patches[1]?.cells).toEqual([{ rowKey: 'r1', columnCode: 'C2', value: 7 }]);
    expect(patches[1]?.status).toBe(200);
    expect([...pendingSlice(Table, Period).keys()]).toEqual(['r1:C1']);
  });

  it('прийнята вставка не лишає по собі незбережених правок', async () => {
    mockServer();
    show();
    await screen.findByTestId('revogrid-stub');

    paste('42');
    await wait(300);

    expect(patches[0]?.status).toBe(200);
    expect(pendingSlice(Table, Period).size).toBe(0);
  });

  it('undo, що не дійшов через збій сервера, лишається незбереженим і повторюваним', async () => {
    mockServer();
    show();
    const grid = await screen.findByTestId('revogrid-stub');

    fireEvent.click(screen.getByRole('button', { name: 'edit-good' }));
    await passDebounce();
    expect(patches[0]?.status).toBe(200);

    forced.push(500);
    fireEvent.keyDown(grid, { key: 'z', ctrlKey: true });
    await wait(300);

    expect(patches[1]?.status).toBe(500);
    expect(patches[1]?.cells).toEqual([{ rowKey: 'r1', columnCode: 'C2', value: 2 }]);

    // ⛔ Доти тут було порожньо: скасування зникало без сліду.
    expect(pendingSlice(Table, Period).get('r1:C2')?.value).toBe(2);

    fireEvent.click(await screen.findByRole('button', { name: /grid\.retrySave/ }));
    await wait(300);

    expect(patches[2]?.cells).toEqual([{ rowKey: 'r1', columnCode: 'C2', value: 2 }]);
    expect(patches[2]?.status).toBe(200);
    expect(pendingSlice(Table, Period).size).toBe(0);
  });

  it('undo перекриває незбережену клавіатурну правку тієї самої комірки, а не навпаки', async () => {
    mockServer();
    show();
    const grid = await screen.findByTestId('revogrid-stub');

    fireEvent.click(screen.getByRole('button', { name: 'edit-good' }));
    await passDebounce();

    // Ще одна правка C2 і одразу Ctrl+Z — до спрацювання дебаунсу.
    fireEvent.click(screen.getByRole('button', { name: 'edit-other-row' }));
    fireEvent.keyDown(grid, { key: 'z', ctrlKey: true });
    await passDebounce();

    // ⛔ Доти незбережена `r2:C2 = 8` лишалась у сховищі й їхала наступним
    // автозбереженням поверх скасування. Тепер сховище несе значення undo.
    const last = patches.at(-1);
    expect(last?.cells).toEqual([{ rowKey: 'r2', columnCode: 'C2', value: 2 }]);
    expect(pendingSlice(Table, Period).size).toBe(0);
  });
});
