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
 * `V-15`: Ctrl+Y (Redo) з клавіатури нічого не зберігав, хоча кнопка Redo
 * працювала.
 *
 * ⛔ Причина — застарілий обробник: `onKeyDown` мав залежності `[pending, save]`
 * без `applyHistory`, тож тримав `applyHistory` (і з ним `data`) того рендера,
 * де його створили. Undo/redo зберігають ПОВЗ сховище правок, `pending` після
 * них не змінюється, і обробник не оновлювався: Ctrl+Y зберігав зі старою
 * версією рядка (сервер — `409`) або, якщо обробник пам'ятав перший рендер
 * без зрізу, не зберігав зовсім.
 *
 * ⚠ Доказ — через ВЕРСІЮ рядка в запиті: мок-сервер щоразу видає нову, і redo
 * мусить іти з тією, що повернув ПОПЕРЕДНІЙ (undo) патч.
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

        // ⚠ Як справжній сервер: чужа (застаріла) версія — конфлікт.
        if (row?.baseVersion !== `v${String(version)}`) {
          return new Response(
            JSON.stringify({ title: 'c', status: 409, errorCode: 'ECR-CELL-0409', correlationId: 'c', conflicts: [] }),
            { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
          );
        }

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

describe('V-15: Ctrl+Z / Ctrl+Y з клавіатури зберігають', () => {
  it('правка → Ctrl+Z → Ctrl+Y: кожен крок — запит з актуальною версією рядка', async () => {
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

    fireEvent.keyDown(grid, { key: 'z', ctrlKey: true });
    await wait(100);
    expect(patches[1]).toEqual({ baseVersion: 'v2', value: 1 });

    fireEvent.keyDown(grid, { key: 'y', ctrlKey: true });
    await wait(100);

    // ⛔ Ось дефект: redo йшов із `v2` — версією ДО undo.
    expect(patches[2]).toEqual({ baseVersion: 'v3', value: 5 });

    // І наступний Ctrl+Z — теж справжній крок, а не порожній.
    fireEvent.keyDown(grid, { key: 'z', ctrlKey: true });
    await wait(100);
    expect(patches[3]).toEqual({ baseVersion: 'v4', value: 1 });
  });
});
