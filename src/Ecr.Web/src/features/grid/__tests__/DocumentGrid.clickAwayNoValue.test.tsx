import { type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDataSchemaModel } from '@revolist/revogrid';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cancelAutosave, useDocumentPending } from '../autosave';
import { pendingSlice, resetPending } from '../pendingStore';
import { createDateCellEditor } from '../DateCellEditor';
import { DocumentGrid } from '../DocumentGrid';

/**
 * AN-39 / L8-07 (рішення людини Q10=A): клік повз редактор списку/довідника/
 * одиниці/Bool - «нічого не обрано», а не «очистити комірку».
 *
 * ⛔ Причина: сітка має `applyOnClose` (T4-03), а ці редактори без `getValue`,
 * тож RevoGrid віддавав `afteredit` з `val === undefined`; `String(undefined ?? '')`
 * -> `''` -> `coerce` -> `null`: збережене значення стиралось одним кліком повз.
 * Редактор дати, навпаки, мусить повертати набране (`getValue`).
 */

let sentVal: unknown = undefined;
let prevented = false;

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: {
    onBeforeedit?: (event: { detail: unknown; preventDefault: () => void }) => void;
    onAfteredit?: (event: { detail: unknown }) => void;
  }) => (
    <div data-testid="revogrid-stub">
      <button
        type="button"
        onClick={() => {
          const detail = { prop: 'C1', model: { __rowKey: 'r1' }, val: sentVal };
          prevented = false;
          props.onBeforeedit?.({
            detail,
            preventDefault: () => {
              prevented = true;
            },
          });
          if (!prevented) props.onAfteredit?.({ detail });
        }}
      >
        close-editor
      </button>
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
  rows: [{ cells: { C1: 1 }, isOrphaned: false, label: null, ordinal: 0, rowKey: 'r1', rowKind: 'Item', rowVersion: 'v1' }],
};

const patches: unknown[] = [];

function Host(): JSX.Element {
  useDocumentPending(12);

  return (
    <DocumentGrid
      documentId={12}
      tableInstanceId={4}
      tableDefId={1}
      periodKey={202609}
      readOnly={false}
      allowsDynamicRows={false}
      maxDynamicRows={null}
    />
  );
}

async function show(): Promise<void> {
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

      return new Response(JSON.stringify(slice), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );

  render(
    <MantineProvider>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <Host />
      </QueryClientProvider>
    </MantineProvider>,
  );
  await screen.findByTestId('revogrid-stub');
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

describe('L8-07: клік повз редактор без вибору', () => {
  it('val === undefined: правку скасовано (preventDefault), збережене значення не стирається', async () => {
    sentVal = undefined;
    await show();

    fireEvent.click(screen.getByRole('button', { name: 'close-editor' }));
    await wait(900);

    expect(prevented).toBe(true);
    expect(pendingSlice(4, 202609).size).toBe(0);
    expect(patches).toHaveLength(0);
  });

  it('T4-03 збережено: текстове/числове поле з набраним значенням + клік повз = КОМІТ', async () => {
    sentVal = '7';
    await show();

    fireEvent.click(screen.getByRole('button', { name: 'close-editor' }));
    await wait(900);

    expect(prevented).toBe(false);
    expect(patches).toHaveLength(1);
    expect(JSON.stringify(patches[0])).toContain('7');
  });

  it('контроль: явне порожнє значення (Delete/очищення) лишається правкою', async () => {
    sentVal = '';
    await show();

    fireEvent.click(screen.getByRole('button', { name: 'close-editor' }));
    await wait(900);

    expect(prevented).toBe(false);
    expect(patches).toHaveLength(1);
  });
});

describe('L8-07: редактор дати повертає набране', () => {
  it('getValue віддає поточне значення поля', () => {
    const instance = createDateCellEditor()({ value: '2026-01-01' } as unknown as ColumnDataSchemaModel, vi.fn());
    const node = document.createElement('div');
    document.body.append(node);
    instance.element = node;
    instance.componentDidRender?.();

    const input = node.querySelector('input');
    expect(input).not.toBeNull();
    if (input === null) return;

    input.value = '2026-09-15';

    expect(instance.getValue?.()).toBe('2026-09-15');
    node.remove();
  });
});
