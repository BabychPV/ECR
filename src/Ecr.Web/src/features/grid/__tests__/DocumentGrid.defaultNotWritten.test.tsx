import { type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { ExplicitCommitEvent } from '../editorTouched';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { captureEdit } from '../edits';
import { cancelAutosave, useDocumentPending } from '../autosave';
import { resetConfirmed } from '../confirmedEdits';
import { pendingSlice, resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * AN-39 / L8-15 (Q10=A / D-283). `defaultValue` порожньої комірки лише ПОКАЗУЄТЬСЯ: клік повз
 * редактор БЕЗ вводу (T4-03 повертає в `afteredit` саме його) нічого не пише й не відкриває
 * модалку підтвердження. Але ЯВНИЙ ввід значення, рівного default (`0` при default `0`), -
 * писати ТРЕБА: інакше це тиха незбережена правка.
 *
 * Заглушка сітки несе обгортку редактора: фокус на полі = відкриття редактора, подія `input` =
 * людина друкує.
 */

let edited = '5';
let defaultValue = '5';
let dataType = 'Int';
let withConfirmation = true;

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: {
    onBeforeedit?: (event: { detail: unknown; preventDefault: () => void }) => void;
    onAfteredit?: (event: { detail: unknown }) => void;
  }) => (
    <div data-testid="revogrid-stub">
      <div className="edit-input-wrapper">
        <input data-testid="editor-input" />
      </div>
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

function column(): ColumnDto {
  return {
    code: 'C1',
    dataType,
    defaultValue,
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

/** `C1` порожня на сервері, є default. */
function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: withConfirmation ? { 'r1:C1': 'Outside the permit window' } : {},
    cellPermissions: {},
    periodKey: Period,
    tableInstanceId: Table,
    columns: [column()],
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

async function show(options: { val: string; def: string; type?: string; confirm?: boolean }): Promise<void> {
  edited = options.val;
  defaultValue = options.def;
  dataType = options.type ?? 'Int';
  withConfirmation = options.confirm ?? true;
  mockServer();

  render(
    <MantineProvider>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <Host />
      </QueryClientProvider>
    </MantineProvider>,
  );
  await screen.findByTestId('revogrid-stub');
}

/** Відкриття редактора: фокус на його полі. */
function openEditor(): HTMLElement {
  const input = screen.getByTestId('editor-input');
  fireEvent.focusIn(input);

  return input;
}

function clickAway(): void {
  fireEvent.click(screen.getByRole('button', { name: 'edit-one' }));
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  resetConfirmed();
  vi.unstubAllGlobals();
});

describe('L8-15: default порожньої комірки', () => {
  it('captureEdit: untouched + default -> не правка; без untouched (явний ввід) -> правка', () => {
    const slice = sliceFixture();

    expect(captureEdit(slice, { columnCode: 'C1', rowKey: 'r1', raw: '5', untouched: true })).toBeNull();
    expect(captureEdit(slice, { columnCode: 'C1', rowKey: 'r1', raw: '5' })?.pending.value).toBe(5);
    expect(captureEdit(slice, { columnCode: 'C1', rowKey: 'r1', raw: '6', untouched: true })?.pending.value).toBe(6);
  });

  it('відкрив редактор і клік повз БЕЗ вводу: без модалки й без PATCH', async () => {
    await show({ val: '5', def: '5' });

    openEditor();
    clickAway();
    await wait(900);

    expect(screen.queryByRole('dialog')).toBeNull();
    expect(pendingSlice(Table, Period).size).toBe(0);
    expect(patches).toHaveLength(0);
  });

  it('явний ввід 0 при default 0 у порожню комірку - ЗБЕРЕЖЕНО', async () => {
    await show({ val: '0', def: '0', confirm: false });

    fireEvent.input(openEditor(), { target: { value: '0' } });
    clickAway();
    await wait(900);

    expect(patches).toHaveLength(1);
  });

  it('список: клік повз без вибору - не записано; явний вибір значення, рівного default, - записано', async () => {
    // Без вибору: те саме, що «відкрив і клік повз» (редактор списку подій вводу не шле).
    await show({ val: '5', def: '5', confirm: false });
    openEditor();
    clickAway();
    await wait(900);
    expect(patches).toHaveLength(0);
    cleanup();
    resetPending();

    // Явний вибір: редактор списку шле `ExplicitCommitEvent` перед save().
    await show({ val: '5', def: '5', confirm: false });
    fireEvent(openEditor(), new CustomEvent(ExplicitCommitEvent, { bubbles: true }));
    clickAway();
    await wait(900);
    expect(patches).toHaveLength(1);
  });

  it('текст, рівний default, набраний явно - збережено', async () => {
    await show({ val: 'N/A', def: 'N/A', type: 'String', confirm: false });

    fireEvent.input(openEditor(), { target: { value: 'N/A' } });
    clickAway();
    await wait(900);

    expect(patches).toHaveLength(1);
  });

  it('редагування, почате набором символа (редактор відкрито вже з ним), - збережено', async () => {
    await show({ val: '0', def: '0', confirm: false });

    fireEvent.keyDown(screen.getByTestId('revogrid-stub'), { key: '0' });
    openEditor();
    clickAway();
    await wait(900);

    expect(patches).toHaveLength(1);
  });

  it('контроль: інше значення в тій самій комірці відкриває модалку підтвердження', async () => {
    await show({ val: '6', def: '5' });

    fireEvent.input(openEditor(), { target: { value: '6' } });
    clickAway();

    expect(await screen.findByRole('dialog')).toBeTruthy();
  });
});
