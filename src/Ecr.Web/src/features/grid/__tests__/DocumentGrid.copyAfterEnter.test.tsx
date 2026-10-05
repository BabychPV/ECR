import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * T4-08 (копія L8-16 для Ctrl+C): Ctrl+C одразу після Enter копіював ПОПЕРЕДНЄ
 * виділення - фокус ще не перейшов. Тепер у вікні коміту копіювання чекає його кінця
 * і пише текст нового виділення через `navigator.clipboard`.
 *
 * Заглушка сітки несе обгортку редактора: Enter у ній відкриває вікно затримки.
 */
vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: () => (
    <div data-testid="revogrid-stub">
      <div className="edit-input-wrapper">
        <input data-testid="editor-input" />
      </div>
    </div>
  ),
}));

const column: ColumnDto = {
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

const slice: TableSliceDto = {
  cellConfirmations: {},
  cellPermissions: {},
  periodKey: 202609,
  tableInstanceId: 4,
  columns: [column],
  rows: [
    { cells: { C1: 11 }, isOrphaned: false, label: null, ordinal: 0, rowKey: 'r1', rowKind: 'Item', rowVersion: 'v1' },
    { cells: { C1: 22 }, isOrphaned: false, label: null, ordinal: 1, rowKey: 'r2', rowKind: 'Item', rowVersion: 'v1' },
  ],
};

async function show(): Promise<HTMLElement> {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response(JSON.stringify(slice), { status: 200, headers: { 'Content-Type': 'application/json' } })),
  );

  render(
    <MantineProvider>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <DocumentGrid
          documentId={12}
          tableInstanceId={4}
          tableDefId={1}
          periodKey={202609}
          readOnly={false}
          allowsDynamicRows={false}
          maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return screen.findByTestId('revogrid-stub');
}

/** RevoGrid повідомляє фокус подією `focuscell` на контейнері (`selection.ts`). */
function focusRow(stub: HTMLElement, y: number): void {
  stub.dispatchEvent(new CustomEvent('focuscell', { bubbles: true, detail: { focus: { x: 0, y } } }));
}

function copy(stub: HTMLElement): ReturnType<typeof vi.fn> {
  const setData = vi.fn();
  fireEvent.copy(stub, { clipboardData: { setData } });

  return setData;
}

async function wait(ms: number): Promise<void> {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, ms));
  });
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('T4-08: Ctrl+C одразу після Enter', () => {
  it('контроль: без вікна коміту копіюється поточне виділення одразу', async () => {
    const stub = await show();
    focusRow(stub, 0);

    expect(copy(stub)).toHaveBeenCalledWith('text/plain', '11\n');
  });

  it('у вікні коміту копіювання чекає його кінця і бере НОВЕ виділення', async () => {
    const writeText = vi.fn(async () => undefined);
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });
    const stub = await show();
    focusRow(stub, 0);

    fireEvent.keyDown(screen.getByTestId('editor-input'), { key: 'Enter' });
    const setData = copy(stub);

    // ⛔ Фокус ще на попередній комірці: копіювати зараз = віддати старе значення.
    expect(setData).not.toHaveBeenCalled();

    // Сітка переводить фокус на наступний рядок - уже після Ctrl+C.
    focusRow(stub, 1);
    await wait(500);

    expect(writeText).toHaveBeenCalledTimes(1);
    expect(writeText).toHaveBeenCalledWith('22\n');
  });
});
