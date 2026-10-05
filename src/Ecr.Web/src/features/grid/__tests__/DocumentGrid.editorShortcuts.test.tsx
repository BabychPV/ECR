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
 * AN-28 / L8-03: Ctrl+Z, Ctrl+Y і Ctrl+V у ВІДКРИТОМУ редакторі комірки
 * належать полю редактора, а не сітці.
 *
 * Дефект: `onKeyDown`/`onPaste` на обгортці сітки не розрізняли, що подія
 * спливла з `<input>` редактора (light DOM RevoGrid). Ctrl+Z у редакторі
 * комірки B скасовував і одразу зберігав останню правку комірки A; Ctrl+V
 * вставляв буфер пакетом у якір замість поля.
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { onAfteredit?: (event: { detail: unknown }) => void }) => (
    <div data-testid="revogrid-stub">
      <button
        type="button"
        onClick={() =>
          props.onAfteredit?.({
            detail: { prop: 'C1', model: { __rowKey: 'r1' }, val: '5' },
          })
        }
      >
        simulate-edit
      </button>
      <div className="edit-input-wrapper">
        <input aria-label="editor" />
      </div>
      <input aria-label="outside-input" />
    </div>
  ),
}));

const DocumentId = 1;
const TableId = 1;
const Period = 202609;

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: Period,
    tableInstanceId: TableId,
    columns: [
      {
        code: 'C1',
        dataType: 'Decimal',
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
      {
        cells: { C1: 1 },
        isOrphaned: false,
        label: null,
        ordinal: 0,
        rowKey: 'r1',
        rowKind: 'Item',
        rowVersion: 'v1',
      },
    ],
  };
}

const patched: string[] = [];

function mockServer(): void {
  patched.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        patched.push(String(init.body));

        return new Response(
          JSON.stringify({ appliedCells: 1, rowVersions: {}, validation: [] }),
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
  useDocumentPending(DocumentId);

  return (
    <DocumentGrid
      documentId={DocumentId}
      tableInstanceId={TableId}
      tableDefId={1}
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
        <Host />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function wait(ms: number): Promise<void> {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, ms));
  });
}

/** Одна збережена правка: PATCH пішов, історія має що скасовувати. */
async function oneSavedEdit(): Promise<void> {
  mockServer();
  show();
  await screen.findByTestId('revogrid-stub');
  fireEvent.click(screen.getByRole('button', { name: 'simulate-edit' }));
  await wait(900);
  expect(patched).toHaveLength(1);
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('AN-28 L8-03: ярлики у відкритому редакторі комірки', () => {
  it('Ctrl+Z у редакторі не скасовує правку сітки (PATCH не йде)', async () => {
    await oneSavedEdit();

    fireEvent.keyDown(screen.getByLabelText('editor'), { key: 'z', ctrlKey: true });
    await wait(900);

    expect(patched).toHaveLength(1);
  });

  it('Ctrl+Y і Ctrl+Shift+Z у редакторі не чіпають сітку', async () => {
    await oneSavedEdit();

    const editor = screen.getByLabelText('editor');
    fireEvent.keyDown(editor, { key: 'z', ctrlKey: true, shiftKey: true });
    fireEvent.keyDown(editor, { key: 'y', ctrlKey: true });
    await wait(900);

    expect(patched).toHaveLength(1);
  });

  it('Ctrl+Z поза редактором, як і раніше, скасовує правку (контроль)', async () => {
    await oneSavedEdit();

    fireEvent.keyDown(screen.getByLabelText('outside-input'), { key: 'z', ctrlKey: true });
    await wait(900);

    expect(patched).toHaveLength(2);
  });

  it('Ctrl+V у редакторі: пакетної вставки немає, paste не скасовано', async () => {
    await oneSavedEdit();

    const notPrevented = fireEvent.paste(screen.getByLabelText('editor'), {
      clipboardData: { getData: () => '7\t8' },
    });
    await wait(900);

    expect(notPrevented).toBe(true);
    expect(patched).toHaveLength(1);
  });
});
