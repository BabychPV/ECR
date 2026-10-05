import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave, useDocumentPending } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * AN-39 / L8-05: ярлики Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z / Ctrl+S — за фізичною
 * клавішею (`event.code`), а не за `event.key`: кирилиця, CapsLock і Shift
 * міняли `key`, і undo/redo/save мовчали (Ctrl+S віддавався браузеру).
 * AltGr (Ctrl+Alt) — друкований символ, не ярлик (T4-07).
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { onAfteredit?: (event: { detail: unknown }) => void }) => (
    <div data-testid="revogrid-stub" tabIndex={0}>
      <button
        type="button"
        onClick={() => props.onAfteredit?.({ detail: { prop: 'C1', model: { __rowKey: 'r1' }, val: '5' } })}
      >
        simulate-edit
      </button>
      <div className="edit-input-wrapper">
        <input data-testid="editor-input" />
      </div>
    </div>
  ),
}));

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [
      {
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
      },
    ],
    rows: [
      { cells: { C1: 1 }, isOrphaned: false, label: null, ordinal: 0, rowKey: 'r1', rowKind: 'Item', rowVersion: 'v1' },
    ],
  };
}

const patches: { baseVersion: string | null; value: unknown }[] = [];

function mockServer(): void {
  patches.length = 0;
  let version = 1;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        const request = JSON.parse(String(init.body)) as {
          rows: { baseVersion: string | null; cells: { value: unknown }[] }[];
        };
        const row = request.rows[0];
        patches.push({ baseVersion: row?.baseVersion ?? null, value: row?.cells[0]?.value });
        version += 1;

        return new Response(
          JSON.stringify({ appliedCells: 1, rowVersions: { r1: `v${String(version)}` }, validation: [] }),
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

function Host(): JSX.Element {
  useDocumentPending(1);

  return (
    <DocumentGrid
      documentId={1}
      tableInstanceId={1}
      tableDefId={1}
      periodKey={202609}
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

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('L8-05: ярлики за event.code', () => {
  it('кирилиця (я/н), CapsLock і Ctrl+Shift+Z: undo → redo; AltGr+Z нічого не скасовує', async () => {
    mockServer();
    render(
      <MantineProvider>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Host />
        </QueryClientProvider>
      </MantineProvider>,
    );

    const grid = await screen.findByTestId('revogrid-stub');

    fireEvent.click(screen.getByRole('button', { name: 'simulate-edit' }));
    await wait(900);
    expect(patches).toEqual([{ baseVersion: 'v1', value: 5 }]);

    // AltGr+Z (польська «ż»): друкований символ — не undo.
    fireEvent.keyDown(grid, { key: 'ż', code: 'KeyZ', ctrlKey: true, altKey: true });
    await wait(100);
    expect(patches).toHaveLength(1);

    // Кирилиця: key = «я», фізична клавіша Z.
    fireEvent.keyDown(grid, { key: 'я', code: 'KeyZ', ctrlKey: true });
    await wait(100);
    expect(patches[1]).toEqual({ baseVersion: 'v2', value: 1 });

    // Ctrl+Shift+Z: key = «Z» (велика) — redo.
    fireEvent.keyDown(grid, { key: 'Z', code: 'KeyZ', ctrlKey: true, shiftKey: true });
    await wait(100);
    expect(patches[2]).toEqual({ baseVersion: 'v3', value: 5 });

    // CapsLock + Ctrl+Z: key = «Z» без Shift — undo.
    fireEvent.keyDown(grid, { key: 'Z', code: 'KeyZ', ctrlKey: true });
    await wait(100);
    expect(patches[3]).toEqual({ baseVersion: 'v4', value: 1 });

    // Кирилиця: Ctrl+Н (клавіша Y) — redo.
    fireEvent.keyDown(grid, { key: 'н', code: 'KeyY', ctrlKey: true });
    await wait(100);
    expect(patches[4]).toEqual({ baseVersion: 'v5', value: 5 });
  });

  it('uk/kz розкладки (я/н/ы) і Ctrl+Z у відкритому редакторі лишається полю', async () => {
    mockServer();
    render(
      <MantineProvider>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Host />
        </QueryClientProvider>
      </MantineProvider>,
    );

    const grid = await screen.findByTestId('revogrid-stub');
    fireEvent.click(screen.getByRole('button', { name: 'simulate-edit' }));
    await wait(900);
    expect(patches).toHaveLength(1);

    // Ctrl+Z у ПОЛІ редактора - не undo сітки й не гаситься.
    const notPrevented = fireEvent.keyDown(screen.getByTestId('editor-input'), { key: 'я', code: 'KeyZ', ctrlKey: true });
    await wait(100);
    expect(notPrevented).toBe(true);
    expect(patches).toHaveLength(1);

    // uk: Z -> «я», Y -> «н», S -> «і»; kz: S -> «ы».
    fireEvent.keyDown(grid, { key: 'я', code: 'KeyZ', ctrlKey: true });
    await wait(100);
    expect(patches).toHaveLength(2);
    fireEvent.keyDown(grid, { key: 'н', code: 'KeyY', ctrlKey: true });
    await wait(100);
    expect(patches).toHaveLength(3);
    expect(fireEvent.keyDown(grid, { key: 'ы', code: 'KeyS', ctrlKey: true })).toBe(false);
    expect(fireEvent.keyDown(grid, { key: 'і', code: 'KeyS', ctrlKey: true })).toBe(false);
  });

  it('Ctrl+І (клавіша S) відміняє дію браузера «зберегти сторінку»', async () => {
    mockServer();
    render(
      <MantineProvider>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Host />
        </QueryClientProvider>
      </MantineProvider>,
    );

    const grid = await screen.findByTestId('revogrid-stub');
    const notPrevented = fireEvent.keyDown(grid, { key: 'і', code: 'KeyS', ctrlKey: true });

    expect(notPrevented).toBe(false);
  });
});
