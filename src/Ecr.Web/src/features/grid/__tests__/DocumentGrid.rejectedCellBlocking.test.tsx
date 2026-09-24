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
 * `V-01` (критично, втрата даних): одна відхилена сервером комірка блокувала
 * збереження всіх наступних правок.
 *
 * ⛔ Відтворено на живому стенді двічі (лінія FTPL): `abc` у колонці `Int` →
 * `422`; правка лишалася в сховищі звичайною незбереженою, і автозбереження
 * везло її з КОЖНИМ наступним пакетом. Сервер часткового застосування не робить
 * — відхиляв усе, і нова правка ІНШОЇ комірки не зберігалась і зникала після
 * перезавантаження.
 *
 * ⚠ Сервер тут — мок, що поводиться як справжній: пакет із нечисловим значенням
 * у `C1` відхиляється ЦІЛКОМ (`ECR-CELL-0422`, `columnCode: 'C1'`), будь-який
 * інший — приймається. Тобто «друга правка збереглась» тут можливо рівно тоді,
 * коли `abc` у другому пакеті НЕМАЄ.
 *
 * ⚠ Шлях — той самий, що в користувача: `afteredit` → сховище → дебаунс
 * документа (`useDocumentPending`, справжній, 500 мс) → зберігач сітки.
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

const has = (cells: readonly SentCell[], rowKey: string, columnCode: string): boolean =>
  cells.some((cell) => cell.rowKey === rowKey && cell.columnCode === columnCode);

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('V-01: відхилена комірка не блокує збереження інших', () => {
  it('abc у Int → відмова; нова правка іншої комірки йде БЕЗ abc і сервер її приймає', async () => {
    mockServer();
    show();
    await screen.findByTestId('revogrid-stub');

    fireEvent.click(screen.getByRole('button', { name: 'edit-bad' }));
    await passDebounce();

    expect(patches).toHaveLength(1);
    expect(patches[0]?.status).toBe(422);

    // Відхилена комірка ПОЗНАЧЕНА: червоний кут і причина — як і було.
    await waitFor(() => expect(screen.getByTestId('cell-r1:C1').dataset['class']).toContain('ecr-cell-save-error'));
    expect(screen.getByTestId('cell-r1:C1').getAttribute('title')).toContain(Refusal);

    // ⛔ Ось сам сценарій дефекту: правка ІНШОЇ комірки того самого рядка.
    fireEvent.click(screen.getByRole('button', { name: 'edit-good' }));
    await passDebounce();

    expect(patches).toHaveLength(2);
    const second = patches[1];
    expect(second === undefined ? [] : second.cells).toEqual([{ rowKey: 'r1', columnCode: 'C2', value: 7 }]);
    expect(has(second?.cells ?? [], 'r1', 'C1')).toBe(false);
    expect(second?.status).toBe(200);

    // Нова правка прийнята й знята зі сховища; відхилена — лишилась із маркером.
    const left = pendingSlice(Table, Period);
    expect([...left.keys()]).toEqual(['r1:C1']);
    expect(screen.getByTestId('cell-r1:C2').textContent).toBe('none');
    expect(screen.getByTestId('cell-r1:C1').dataset['class']).toContain('ecr-cell-save-error');

    // ⛔ І в комірці видно САМЕ відхилене значення, а не старе збережене.
    expect(screen.getByTestId('shown-r1:C1').textContent).toBe('abc');

    // Причина й «Retry save» не зникли через УСПІХ чужого пакета.
    expect(screen.getByText(Refusal)).toBeTruthy();
    expect(screen.getByRole('button', { name: /grid\.retrySave/ })).toBeTruthy();
  });

  it('відхилена повторюється за явною дією — «Retry save»', async () => {
    mockServer();
    show();
    await screen.findByTestId('revogrid-stub');

    fireEvent.click(screen.getByRole('button', { name: 'edit-bad' }));
    await passDebounce();
    fireEvent.click(await screen.findByRole('button', { name: /grid\.retrySave/ }));
    await wait(100);

    expect(patches).toHaveLength(2);
    expect(has(patches[1]?.cells ?? [], 'r1', 'C1')).toBe(true);
  });

  it('нова правка ЦІЄЇ комірки знімає утримання й доїжджає', async () => {
    mockServer();
    show();
    await screen.findByTestId('revogrid-stub');

    fireEvent.click(screen.getByRole('button', { name: 'edit-bad' }));
    await passDebounce();

    fireEvent.click(screen.getByRole('button', { name: 'edit-fixed' }));
    await passDebounce();

    expect(patches[1]?.cells).toEqual([{ rowKey: 'r1', columnCode: 'C1', value: 42 }]);
    expect(patches[1]?.status).toBe(200);
    expect(pendingSlice(Table, Period).size).toBe(0);
    await waitFor(() => expect(screen.queryByText(Refusal)).toBeNull());
  });

  it('навіть те саме значення, введене в ЦЮ комірку вдруге, — явна дія, і воно йде знову', async () => {
    mockServer();
    show();
    await screen.findByTestId('revogrid-stub');

    fireEvent.click(screen.getByRole('button', { name: 'edit-bad' }));
    await passDebounce();
    fireEvent.click(screen.getByRole('button', { name: 'edit-bad' }));
    await passDebounce();

    expect(patches).toHaveLength(2);
    expect(patches[1]?.cells).toEqual([{ rowKey: 'r1', columnCode: 'C1', value: 'abc' }]);
  });

  it('збережене значення, набране назад, скасовує відхилену правку — без запиту', async () => {
    mockServer();
    show();
    await screen.findByTestId('revogrid-stub');

    fireEvent.click(screen.getByRole('button', { name: 'edit-bad' }));
    await passDebounce();
    expect(pendingSlice(Table, Period).size).toBe(1);

    fireEvent.click(screen.getByRole('button', { name: 'edit-revert' }));
    await passDebounce();

    expect(patches).toHaveLength(1);
    expect(pendingSlice(Table, Period).size).toBe(0);
    await waitFor(() => expect(screen.queryByRole('button', { name: /grid\.retrySave/ })).toBeNull());
    expect(screen.getByTestId('cell-r1:C1').dataset['class']).not.toContain('ecr-cell-save-error');
  });

  it('правильну правку, відхилену РАЗОМ з поганою, довозить окремий пакет', async () => {
    mockServer();
    show();
    await screen.findByTestId('revogrid-stub');

    // Дві правки в одному вікні дебаунсу — один пакет, відхилений цілком.
    fireEvent.click(screen.getByRole('button', { name: 'edit-bad' }));
    fireEvent.click(screen.getByRole('button', { name: 'edit-other-row' }));
    await passDebounce();

    expect(patches[0]?.status).toBe(422);
    expect(has(patches[0]?.cells ?? [], 'r2', 'C2')).toBe(true);

    // ⛔ Без повтору `r2:C2` чекав би наступної правки — або зник би з
    // перезавантаженням.
    await passDebounce();

    expect(patches).toHaveLength(2);
    expect(patches[1]?.cells).toEqual([{ rowKey: 'r2', columnCode: 'C2', value: 8 }]);
    expect(patches[1]?.status).toBe(200);
    expect([...pendingSlice(Table, Period).keys()]).toEqual(['r1:C1']);
  });
});
