import { type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnRegular } from '@revolist/revogrid';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cancelAutosave, useDocumentPending } from '../autosave';
import { pendingSlice, resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * `B-09`: повтор після відмови везе ЧИННУ версію рядка, а конфлікт версії має
 * вихід — «Keep mine» / «Discard mine».
 *
 * ⛔ Що ламалося, живцем на стенді. `baseVersion` запам'ятовувалась у мить
 * введення (`edits.captureEdit`) і їхала такою назавжди, а `buildRequest` брав
 * її з першої правки рядка. Правка, яку сервер раз відхилив, чекала повтору зі
 * СТАРОЮ версією, хоча сусідня комірка того самого рядка вже зберіглась і
 * підняла версію: «Retry save» діставав `409` — і так при кожному натисканні.
 * А справжній конфлікт (чужа правка) взагалі не мав виходу: панель показувала
 * чуже значення, а повтор ішов тією самою старою версією.
 *
 * ⚠ Мок-сервер тут ПЕРЕВІРЯЄ версію, на відміну від `conflictHold.test.tsx`:
 * саме без цього стара версія й лишалась непоміченою жодним тестом.
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: {
    source?: Record<string, unknown>[];
    columns?: ColumnRegular[];
    onAfteredit?: (event: { detail: unknown }) => void;
  }) => (
    <div data-testid="revogrid-stub">
      {[
        { id: 'c1-42', column: 'C1', val: '42' },
        { id: 'c2-7', column: 'C2', val: '7' },
      ].map((cell) => (
        <button
          key={cell.id}
          type="button"
          onClick={() =>
            props.onAfteredit?.({ detail: { prop: cell.column, model: { __rowKey: 'r1' }, val: cell.val } })
          }
        >
          {`edit-${cell.id}`}
        </button>
      ))}
      <span data-testid="shown-C1">
        {String((props.source ?? []).find((row) => row['__rowKey'] === 'r1')?.['C1'] ?? '')}
      </span>
    </div>
  ),
}));

const DocumentId = 9;
const Table = 1;
const Period = 202609;

function column(code: string, ordinal: number): ColumnDto {
  return {
    code,
    dataType: 'Int',
    defaultValue: null,
    displayFormat: null,
    header: `Header ${code}`,
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

/** Стан «сервера»: одна версія й значення рядка `r1`. */
interface ServerRow {
  version: string;
  cells: Record<string, unknown>;
}

let server: ServerRow;

/** Скільки разів відмовити `C1` значенням `422` (тимчасова відмова). */
let refuseC1: number;

const patches: { baseVersion: string | null; cells: string[]; status: number }[] = [];
let sliceReads = 0;

function sliceOf(row: ServerRow): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: Period,
    tableInstanceId: Table,
    columns: [column('C1', 0), column('C2', 1)],
    rows: [
      {
        cells: { ...row.cells },
        isOrphaned: false,
        label: 'Row one',
        ordinal: 0,
        rowKey: 'r1',
        rowKind: 'Item',
        rowVersion: row.version,
      },
    ],
  };
}

function json(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

function mockServer(initial: ServerRow, shown: ServerRow = initial): void {
  server = initial;
  patches.length = 0;
  sliceReads = 0;

  // ⚠ Перший зріз — те, що бачив екран; далі — те, що є на «сервері».
  let firstRead = true;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method !== 'PATCH') {
        sliceReads += 1;
        const row = firstRead ? shown : server;
        firstRead = false;

        return json(sliceOf(row), 200);
      }

      const request = JSON.parse(String(init.body)) as {
        rows: { rowKey: string; baseVersion: string | null; cells: { columnCode: string; value: unknown }[] }[];
      };
      const [row] = request.rows;
      const cells = row?.cells.map((cell) => cell.columnCode) ?? [];

      if (row?.baseVersion !== server.version) {
        patches.push({ baseVersion: row?.baseVersion ?? null, cells, status: 409 });

        return json(
          {
            title: 'Conflict',
            status: 409,
            detail: 'stale',
            errorCode: 'ECR-CELL-0409',
            correlationId: 'c1',
            conflicts: (row?.cells ?? []).map((cell) => ({
              rowKey: 'r1',
              columnCode: cell.columnCode,
              yourValue: cell.value,
              theirValue: server.cells[cell.columnCode] ?? null,
              theirUser: 'A. Serikbayev',
              theirOrigin: 'UserEdit',
              theirChangedAt: null,
              currentVersion: server.version,
            })),
          },
          409,
        );
      }

      if (cells.includes('C1') && refuseC1 > 0) {
        refuseC1 -= 1;
        patches.push({ baseVersion: row.baseVersion, cells, status: 422 });

        return json(
          { title: 'Invalid', status: 422, detail: 'C1 refused', errorCode: 'ECR-CELL-0422', correlationId: 'c2', columnCode: 'C1' },
          422,
        );
      }

      patches.push({ baseVersion: row.baseVersion, cells, status: 200 });
      for (const cell of row.cells) server.cells[cell.columnCode] = cell.value;
      server.version = `${server.version}+`;

      return json({ appliedCells: cells.length, rowVersions: { r1: server.version }, validation: [] }, 200);
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
  refuseC1 = 0;
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('B-09: повтор після відмови — з чинною версією рядка', () => {
  it('відхилена правка, повторена після збереження сусідньої комірки, їде з НОВОЮ версією і приймається', async () => {
    refuseC1 = 1;
    mockServer({ version: 'v1', cells: { C1: 1, C2: 2 } });
    show();
    await screen.findByTestId('revogrid-stub');

    fireEvent.click(screen.getByRole('button', { name: 'edit-c1-42' }));
    await passDebounce();
    expect(patches[0]).toEqual({ baseVersion: 'v1', cells: ['C1'], status: 422 });

    // Сусідня комірка того самого рядка зберігається й піднімає версію.
    fireEvent.click(screen.getByRole('button', { name: 'edit-c2-7' }));
    await passDebounce();
    expect(patches[1]).toEqual({ baseVersion: 'v1', cells: ['C2'], status: 200 });

    fireEvent.click(await screen.findByTestId('grid-retry-save'));
    await wait(300);

    // ⛔ До виправлення тут їхало `v1` і сервер відповідав `409` — щоразу.
    expect(patches[2]).toEqual({ baseVersion: 'v1+', cells: ['C1'], status: 200 });
    expect(pendingSlice(Table, Period).size).toBe(0);
  });
});

describe('B-09: справжній конфлікт — моє проти чинного, і два виходи', () => {
  it('панель показує обидва значення; «Keep mine» повторює з версією від сервера й перечитує зріз', async () => {
    // Екран бачив `v1`, а хтось уже записав 99 → `v5`.
    mockServer({ version: 'v5', cells: { C1: 99, C2: 2 } }, { version: 'v1', cells: { C1: 1, C2: 2 } });
    show();
    await screen.findByTestId('revogrid-stub');

    fireEvent.click(screen.getByRole('button', { name: 'edit-c1-42' }));
    await passDebounce();
    expect(patches[0]?.status).toBe(409);

    const panel = await screen.findByTestId('grid-conflict');

    // ⚠ Каталог у тестах порожній: `t()` віддає `⟦ключ (параметри)⟧`, тож
    // значення видно саме як параметри підпису.
    expect(panel.textContent).toContain('yours=42');
    expect(panel.textContent).toContain('value=99');
    expect(panel.textContent).toContain('row=Row one');
    expect(panel.textContent).toContain('column=Header C1');

    const readsBefore = sliceReads;
    fireEvent.click(screen.getByTestId('grid-conflict-keep'));
    await wait(300);

    expect(patches[1]).toEqual({ baseVersion: 'v5', cells: ['C1'], status: 200 });
    expect(pendingSlice(Table, Period).size).toBe(0);
    expect(sliceReads).toBeGreaterThan(readsBefore);
    expect(screen.queryByTestId('grid-conflict')).toBeNull();
  });

  it('«Discard mine» нічого не шле, знімає мою правку й показує чинне значення', async () => {
    mockServer({ version: 'v5', cells: { C1: 99, C2: 2 } }, { version: 'v1', cells: { C1: 1, C2: 2 } });
    show();
    await screen.findByTestId('revogrid-stub');

    fireEvent.click(screen.getByRole('button', { name: 'edit-c1-42' }));
    await passDebounce();

    fireEvent.click(await screen.findByTestId('grid-conflict-discard'));
    await wait(300);

    expect(patches).toHaveLength(1);
    expect(pendingSlice(Table, Period).size).toBe(0);
    expect(screen.getByTestId('shown-C1').textContent).toBe('99');
    expect(screen.queryByTestId('grid-conflict')).toBeNull();
  });

  it('панель не гасне від збереження ІНШОЇ комірки, доки конфліктна не розв\'язана', async () => {
    mockServer({ version: 'v5', cells: { C1: 99, C2: 2 } }, { version: 'v1', cells: { C1: 1, C2: 2 } });
    show();
    await screen.findByTestId('revogrid-stub');

    fireEvent.click(screen.getByRole('button', { name: 'edit-c1-42' }));
    await passDebounce();
    await screen.findByTestId('grid-conflict');

    fireEvent.click(screen.getByRole('button', { name: 'edit-c2-7' }));
    await passDebounce();

    expect(screen.getByTestId('grid-conflict')).toBeTruthy();
  });
});
