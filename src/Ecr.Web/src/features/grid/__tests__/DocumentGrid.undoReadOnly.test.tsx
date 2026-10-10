import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { inFlightRowKeys } from '../inFlightEdits';
import { pendingCount, resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * F6-04: Undo/Redo не пишуть у аркуш, який став лише для читання (подано,
 * закритий період, вузький екран). Сітка лишається змонтованою, стек історії
 * живе далі — тож доти Ctrl+Z і кнопка слали PATCH у поданий аркуш.
 *
 * ⚠ Автозбереження приглушене: порядок запитів задає тест (Ctrl+S, Ctrl+Z).
 */
vi.mock('../autosave', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../autosave')>();

  return { ...actual, scheduleAutosave: (): void => undefined };
});

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

function grid(readOnly: boolean): JSX.Element {
  return (
    <DocumentGrid
      documentId={1}
      tableInstanceId={1}
      tableDefId={1}
      periodKey={202609}
      readOnly={readOnly}
      allowsDynamicRows={false}
      maxDynamicRows={null}
    />
  );
}

function show(): (readOnly: boolean) => void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrap = (readOnly: boolean): JSX.Element => (
    <MantineProvider>
      <QueryClientProvider client={client}>{grid(readOnly)}</QueryClientProvider>
    </MantineProvider>
  );
  const { rerender } = render(wrap(false));

  return (readOnly: boolean) => {
    rerender(wrap(readOnly));
  };
}

async function press(key: string, code: string): Promise<void> {
  await act(async () => {
    fireEvent.keyDown(screen.getByTestId('revogrid-stub'), { key, code, ctrlKey: true });
    await Promise.resolve();
  });
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('DocumentGrid: Undo/Redo у режимі лише для читання (F6-04)', () => {
  it('аркуш став лише для читання — Ctrl+Z і кнопка нічого не пишуть, а крок не губиться', async () => {
    mockServer();
    const setReadOnly = show();
    await screen.findByTestId('revogrid-stub');
    fireEvent.click(screen.getByRole('button', { name: 'type-5' }));
    await press('s', 'KeyS');
    await waitFor(() => expect(patches).toHaveLength(1));
    await waitFor(() => expect(inFlightRowKeys(1, 202609).size).toBe(0));

    // Подано: сітка та сама, `readOnly` став true.
    setReadOnly(true);
    await press('z', 'KeyZ');
    await press('y', 'KeyY');
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });

    // ⛔ Мутація: прибрати перевірку `readOnly` в `applyHistory`/`onKeyDown` — тут другий PATCH.
    expect(patches).toHaveLength(1);
    expect(pendingCount()).toBe(0);
    expect((screen.getByRole('button', { name: '⟦grid.undo⟧' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: '⟦grid.redo⟧' }) as HTMLButtonElement).disabled).toBe(true);

    // Знову редагований — крок на місці (Ctrl+Z у readOnly його не з'їв).
    setReadOnly(false);
    await press('z', 'KeyZ');
    await waitFor(() => expect(patches).toHaveLength(2));
    expect(Number(patches[1]?.[0]?.cells[0]?.value)).toBe(1);
  });
});
