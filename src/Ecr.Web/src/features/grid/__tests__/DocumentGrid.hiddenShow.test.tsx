import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, PatchCellsRequest, PatchCellsResponse, TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { invalidateSlices } from '../sliceCache';
import { applyPatchLocally } from '../useCellPatch';
import { DocumentGrid } from '../DocumentGrid';

/**
 * `X2-05` (C1-01): повернення до прихованої сітки перечитувало зріз після КОЖНОГО власного
 * збереження — `applyPatchLocally` лишає зріз `isInvalidated` (позначка без запиту), а сітка
 * дивилась лише на неї. Перечитати треба лише те, що пропустила чужа інвалідація (перерахунок,
 * імпорт — `invalidateSlices`), поки сітка була прихована.
 */
vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: () => <div data-testid="revogrid-stub" />,
}));

const Document = 3;
const Table = 1;
const Period = 202609;

function column(): ColumnDto {
  return {
    code: 'C1',
    dataType: 'Int',
    defaultValue: null,
    displayFormat: null,
    header: 'C1',
    id: 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    lookupRegistryDefId: null,
    ordinal: 0,
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
    columns: [column()],
    rows: [
      { cells: { C1: 1 }, isOrphaned: false, label: null, ordinal: 0, rowKey: 'r1', rowVersion: 'v1', rowKind: 'Item' as const },
    ],
  };
}

let reads = 0;

function mockServer(): void {
  reads = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(() => {
      reads += 1;

      return Promise.resolve(
        new Response(JSON.stringify(sliceFixture()), { status: 200, headers: { 'Content-Type': 'application/json' } }),
      );
    }),
  );
}

function grid(hidden: boolean): JSX.Element {
  return (
    <DocumentGrid
      documentId={Document}
      tableInstanceId={Table}
      tableDefId={1}
      periodKey={Period}
      readOnly={false}
      allowsDynamicRows={false}
      maxDynamicRows={null}
      hidden={hidden}
    />
  );
}

async function hiddenGrid(): Promise<{ client: QueryClient; show: () => void }> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const tree = (hidden: boolean): JSX.Element => (
    <MantineProvider>
      <QueryClientProvider client={client}>{grid(hidden)}</QueryClientProvider>
    </MantineProvider>
  );
  const view = render(tree(true));
  await screen.findByTestId('revogrid-stub');
  await waitFor(() => expect(reads).toBe(1));

  return {
    client,
    show: () => {
      view.rerender(tree(false));
    },
  };
}

const OwnRequest: PatchCellsRequest = {
  tableInstanceId: Table,
  periodKey: Period,
  rows: [{ rowKey: 'r1', baseVersion: 'v1', cells: [{ columnCode: 'C1', value: 5 }] }],
} as unknown as PatchCellsRequest;

const OwnResponse = { appliedCells: 1, rowVersions: { r1: 'v2' }, validation: [] } as unknown as PatchCellsResponse;

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('DocumentGrid: показ прихованої сітки (X2-05)', () => {
  it('власне збереження до приховування не коштує перечитування зрізу при показі', async () => {
    mockServer();
    const { client, show } = await hiddenGrid();

    act(() => {
      applyPatchLocally(client, OwnRequest, OwnResponse);
    });
    expect(client.getQueryState(['table-slice', Table, Period])?.isInvalidated).toBe(true);

    act(show);
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 100));
    });

    // ⛔ Мутація: повернути перевірку лише за `isInvalidated` — тут буде 2.
    expect(reads).toBe(1);
  });

  it('пропущена інвалідація (перерахунок, поки сітка прихована) — зріз перечитується при показі', async () => {
    mockServer();
    const { client, show } = await hiddenGrid();

    await act(async () => {
      await invalidateSlices(client, { documentId: Document, periodKey: Period });
    });
    expect(reads).toBe(1);

    act(show);

    await waitFor(() => expect(reads).toBe(2));
  });
});
