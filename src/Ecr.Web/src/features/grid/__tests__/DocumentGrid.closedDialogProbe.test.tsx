import { type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cancelAutosave, useDocumentPending } from '../autosave';
import { markConfirmed, resetConfirmed } from '@/features/grid/confirmedEdits';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';
import { renderKeepMounted } from '@/test/closedDialogProbe';

/**
 * B149, група «null-захист в обробнику»: підтвердження правки поза вікном доступу (`ФВ-2.16`) — одиничне
 * («confirm edit») і пакетне («batch confirm») — після закриття (діалог ще в DOM) не застосовує правку вдруге.
 *
 * Кнопка діалогу запиту сама не шле: вона позначає комірки «підтверджено» (`markConfirmed`) і застосовує
 * правку, а `PATCH` будує автозбереження. Тому зонд рахує ОБИДВА наслідки: виклики `markConfirmed` (скільки
 * разів обробник справді спрацював) і `PATCH` на сервер.
 *
 * Зонд (справжня сітка із заглушкою RevoGrid, `keepMountedModalsTheme`): «Proceed» → діалог закрито → клік по
 * «Proceed» закритого діалогу. ЗАКРИТО: `onConfirmEdit` — `if (confirmRequest === null) return`;
 * `onConfirmBatch` — `request?.apply()` на `null`. Один `markConfirmed`, один `PATCH`.
 *
 * ⚠ Очікування — справжній час (дебаунс автозбереження 500 мс), як у `DocumentGrid.batchConfirm.test.tsx`:
 * результат від таймінгу не залежить, лише чекає, доки автозбереження відпрацює.
 */

vi.mock('@/features/grid/confirmedEdits', async (importOriginal) => {
  const real = await importOriginal<typeof import('@/features/grid/confirmedEdits')>();

  return { ...real, markConfirmed: vi.fn(real.markConfirmed) };
});

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: {
    onBeforeedit?: (event: { detail: unknown; preventDefault: () => void }) => void;
    onBeforerangeedit?: (event: { detail: unknown; preventDefault: () => void }) => void;
    onAfteredit?: (event: { detail: unknown }) => void;
  }) => (
    <div data-testid="revogrid-stub">
      <button
        type="button"
        onClick={() => {
          const detail = { data: { 0: { C1: 9, C2: 9 }, 1: { C1: 9, C2: 9 } }, models: { 0: { __rowKey: 'r1' }, 1: { __rowKey: 'r2' } }, type: 'rgRow' };
          let prevented = false;
          props.onBeforerangeedit?.({ detail, preventDefault: () => (prevented = true) });
          if (!prevented) props.onAfteredit?.({ detail });
        }}
      >
        fill
      </button>
      <button
        type="button"
        onClick={() => {
          const detail = { prop: 'C1', model: { __rowKey: 'r1' }, val: '5' };
          let prevented = false;
          props.onBeforeedit?.({ detail, preventDefault: () => (prevented = true) });
          if (!prevented) props.onAfteredit?.({ detail });
        }}
      >
        edit-one
      </button>
    </div>
  ),
}));

const Table = 4;
const Period = 202609;

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

const slice: TableSliceDto = {
  cellConfirmations: { 'r1:C1': 'Outside the permit window', 'r2:C1': 'Outside the permit window' },
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

const patches: string[][] = [];

function mockServer(): void {
  patches.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        const request = JSON.parse(String(init.body)) as { rows: { rowKey: string; cells: { columnCode: string }[] }[] };
        patches.push(request.rows.flatMap((row) => row.cells.map((cell) => `${row.rowKey}:${cell.columnCode}`)).sort());

        return new Response(
          JSON.stringify({ appliedCells: 1, rowVersions: Object.fromEntries(request.rows.map((row) => [row.rowKey, 'v2'])), validation: [] }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      return new Response(JSON.stringify(slice), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

function Host(): JSX.Element {
  useDocumentPending(12);

  return (
    <DocumentGrid
      documentId={12}
      tableInstanceId={Table}
      tableDefId={1}
      periodKey={Period}
      readOnly={false}
      allowsDynamicRows={false}
      maxDynamicRows={null}
    />
  );
}

async function wait(ms: number): Promise<void> {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, ms));
  });
}

const Proceed = /grid\.confirmProceed/;

async function proceedTwice(trigger: string): Promise<void> {
  mockServer();
  renderKeepMounted(<Host />);
  fireEvent.click(await screen.findByRole('button', { name: trigger }));

  const dialog = await screen.findByRole('dialog');
  const proceed = within(dialog).getByRole('button', { name: Proceed });
  fireEvent.click(proceed);
  await waitFor(() => {
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  // Клік по «Proceed» діалогу, що закрився: вузол на місці.
  expect(proceed.isConnected).toBe(true);
  fireEvent.click(proceed);
  await wait(900);
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  resetConfirmed();
  vi.mocked(markConfirmed).mockClear();
  vi.unstubAllGlobals();
});

describe('B149: DocumentGrid — підтвердження правки, що закрилося, не застосовує її вдруге', () => {
  it('confirm edit: один markConfirmed і один PATCH', async () => {
    await proceedTwice('edit-one');

    expect(vi.mocked(markConfirmed)).toHaveBeenCalledTimes(1);
    expect(patches).toEqual([['r1:C1']]);
  });

  it('batch confirm: один markConfirmed і один PATCH усього пакета', async () => {
    await proceedTwice('fill');

    expect(vi.mocked(markConfirmed)).toHaveBeenCalledTimes(1);
    expect(patches).toEqual([['r1:C1', 'r1:C2', 'r2:C1', 'r2:C2']]);
  });
});
