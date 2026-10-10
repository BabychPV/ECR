import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { queryKeys } from '@/api/queryKeys';
import { showWarning } from '@/shared/ui/notify';
import { cancelAutosave } from '../autosave';
import { inFlightRowKeys } from '../inFlightEdits';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * `G1-05`: Undo скасовує СВІЙ крок, а не те, що відтоді записав хтось інший.
 * Доти крок ішов наосліп зі свіжою версією рядка й без `before`, і сервер
 * приймав запис поверх чужого значення, що вже було на екрані.
 *
 * ⚠ Автозбереження приглушене: порядок запитів задає тест (Ctrl+S, Ctrl+Z).
 */
vi.mock('../autosave', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../autosave')>();

  return { ...actual, scheduleAutosave: (): void => undefined };
});

vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showWarning: vi.fn(),
}));

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { onAfteredit?: (event: { detail: unknown }) => void }) => (
    <div data-testid="revogrid-stub">
      <button
        type="button"
        onClick={() => props.onAfteredit?.({ detail: { prop: 'C1', model: { __rowKey: 'r1' }, val: '5' } })}
      >
        type-5
      </button>
    </div>
  ),
}));

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

function slice(value: number, version: string): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [column()],
    rows: [
      {
        cells: { C1: value },
        isOrphaned: false,
        label: null,
        ordinal: 0,
        rowKey: 'r1',
        rowVersion: version,
        rowKind: 'Item' as const,
      },
    ],
  };
}

const patches: { cells: { columnCode: string; value: unknown }[] }[][] = [];

function mockServer(): void {
  patches.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn((_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        patches.push((JSON.parse(String(init.body)) as { rows: { cells: { columnCode: string; value: unknown }[] }[] }).rows);

        return Promise.resolve(
          new Response(JSON.stringify({ appliedCells: 1, rowVersions: { r1: `v${String(patches.length + 1)}` }, validation: [] }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
        );
      }

      return Promise.resolve(
        new Response(JSON.stringify(slice(1, 'v1')), { status: 200, headers: { 'Content-Type': 'application/json' } }),
      );
    }),
  );
}

function show(): QueryClient {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentGrid
          documentId={1}
          tableInstanceId={1}
          tableDefId={1}
          periodKey={202609}
          readOnly={false}
          allowsDynamicRows={false}
          maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return client;
}

async function press(key: string, code: string): Promise<void> {
  await act(async () => {
    fireEvent.keyDown(screen.getByTestId('revogrid-stub'), { key, code, ctrlKey: true });
    await Promise.resolve();
  });
}

/** Крок: C1 1 → 5, збережено (сервер підняв версію). */
async function editAndSave(): Promise<void> {
  await screen.findByTestId('revogrid-stub');
  fireEvent.click(screen.getByRole('button', { name: 'type-5' }));
  await press('s', 'KeyS');
  await waitFor(() => expect(patches).toHaveLength(1));
  // ⚠ Запит надіслано — ще не означає, що відповідь застосовано: `Response.json`
  // розбирається кілька тактів, і запізніла відповідь лягла б у кеш (`C1 = 5`,
  // `applyPatchLocally`) ПОВЕРХ «чужого» зрізу, який тест кладе далі, — чужої
  // правки на екрані вже не було б. Тож крок завершено, лише коли запит пішов з дороги.
  await waitFor(() => expect(inFlightRowKeys(1, 202609).size).toBe(0));
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
  vi.mocked(showWarning).mockClear();
});

describe('DocumentGrid: Undo не затирає чужу правку (G1-05)', () => {
  it('комірку відтоді змінив хтось інший — Undo її не чіпає й каже про це', async () => {
    mockServer();
    const client = show();
    await editAndSave();

    // Колега записав 9; перезапит зрізу приніс це на екран.
    await act(async () => {
      client.setQueryData(queryKeys.slices.one(1, 202609), slice(9, 'v9'));
      // ⚠ `notifyManager` TanStack розсилає зміну спостерігачам через
      // `setTimeout(0)`: без паузи Ctrl+Z ішов би обробником ПОПЕРЕДНЬОГО рендера,
      // де в комірці ще власне 5, — і законно відкочував би (той самий прийом,
      // що `refetch` у `RulesMatrixPanel.unsaved.test.tsx`).
      await new Promise((resolve) => setTimeout(resolve, 50));
    });

    await press('z', 'KeyZ');

    // ⛔ Мутація: прибрати звірку в `applyHistory` — тут другий PATCH з C1=1.
    expect(patches).toHaveLength(1);
    expect(vi.mocked(showWarning)).toHaveBeenCalledWith('⟦grid.undoChangedSince (count=1)⟧');
  });

  it('контроль: у комірці досі значення кроку — Undo відкочує як завжди', async () => {
    mockServer();
    show();
    await editAndSave();

    await press('z', 'KeyZ');

    await waitFor(() => expect(patches).toHaveLength(2));
    expect(Number(patches[1]?.[0]?.cells[0]?.value)).toBe(1);
    expect(vi.mocked(showWarning)).not.toHaveBeenCalled();
  });
});
