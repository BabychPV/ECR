import { type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { captureEdit } from '../edits';
import { cancelAutosave, useDocumentPending } from '../autosave';
import { resetConfirmed } from '../confirmedEdits';
import { pendingSlice, resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * AN-39 / L8-15 (рішення людини Q10=A): `defaultValue` порожньої комірки лише
 * ПОКАЗУЄТЬСЯ. Клік повз редактор повертає в `afteredit` саме його (T4-03) - це
 * не введення: нічого не пишеться і модалку підтвердження не відкриває.
 */

let edited = '5';

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: {
    onBeforeedit?: (event: { detail: unknown; preventDefault: () => void }) => void;
    onAfteredit?: (event: { detail: unknown }) => void;
  }) => (
    <div data-testid="revogrid-stub">
      <button
        type="button"
        onClick={() => {
          const detail = { prop: 'C1', model: { __rowKey: 'r1' }, val: edited };
          let prevented = false;
          props.onBeforeedit?.({
            detail,
            preventDefault: () => {
              prevented = true;
            },
          });
          if (!prevented) props.onAfteredit?.({ detail });
        }}
      >
        edit-one
      </button>
    </div>
  ),
}));

const DocumentId = 12;
const Table = 4;
const Period = 202609;

const column: ColumnDto = {
  code: 'C1',
  dataType: 'Int',
  defaultValue: '5',
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

/** `C1` порожня на сервері, default = 5, правка поза вікном дозволу (потрібне підтвердження). */
function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: { 'r1:C1': 'Outside the permit window' },
    cellPermissions: {},
    periodKey: Period,
    tableInstanceId: Table,
    columns: [column],
    rows: [{ cells: {}, isOrphaned: false, label: null, ordinal: 0, rowKey: 'r1', rowKind: 'Item', rowVersion: 'v1' }],
  };
}

const patches: unknown[] = [];

function mockServer(): void {
  patches.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        patches.push(JSON.parse(String(init.body)));

        return new Response(JSON.stringify({ appliedCells: 1, rowVersions: { r1: 'v2' }, validation: [] }), {
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

function Host(): JSX.Element {
  useDocumentPending(DocumentId);

  return (
    <DocumentGrid
      documentId={DocumentId}
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

async function show(): Promise<void> {
  render(
    <MantineProvider>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <Host />
      </QueryClientProvider>
    </MantineProvider>,
  );
  await screen.findByTestId('revogrid-stub');
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  resetConfirmed();
  vi.unstubAllGlobals();
});

describe('L8-15: default порожньої комірки не пишеться', () => {
  it('captureEdit: default у порожній комірці - не правка; інше значення - правка', () => {
    const slice = sliceFixture();

    expect(captureEdit(slice, { columnCode: 'C1', rowKey: 'r1', raw: '5' })).toBeNull();
    expect(captureEdit(slice, { columnCode: 'C1', rowKey: 'r1', raw: '6' })?.pending.value).toBe(6);
  });

  it('клік повз редактор з показаним default: без модалки підтвердження й без PATCH', async () => {
    mockServer();
    edited = '5';
    await show();

    fireEvent.click(screen.getByRole('button', { name: 'edit-one' }));
    await wait(900);

    expect(screen.queryByRole('dialog')).toBeNull();
    expect(pendingSlice(Table, Period).size).toBe(0);
    expect(patches).toHaveLength(0);
  });

  it('контроль: інше значення в тій самій комірці відкриває модалку', async () => {
    mockServer();
    edited = '6';
    await show();

    fireEvent.click(screen.getByRole('button', { name: 'edit-one' }));

    expect(await screen.findByRole('dialog')).toBeTruthy();
  });
});
