import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { showWarning } from '@/shared/ui/notify';
import { cancelAutosave } from '../autosave';
import { inFlightRowKeys } from '../inFlightEdits';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * `X2-01`: дві правки однієї комірки до збереження (або повернення до
 * збереженого за `V-01`). «Було» кроку бралось із КЕШУ, а не з показаного:
 * «10 → 20 → 25» давало крок `{10→25}` — Undo писав 10, якого на екрані не було,
 * а наступний Undo хибно казав «змінено після кроку» (`grid.undoChangedSince`).
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
      {['20', '25', '10'].map((val) => (
        <button
          key={val}
          type="button"
          onClick={() => props.onAfteredit?.({ detail: { prop: 'C1', model: { __rowKey: 'r1' }, val } })}
        >
          {`type-${val}`}
        </button>
      ))}
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
        new Response(JSON.stringify(slice(10, 'v1')), { status: 200, headers: { 'Content-Type': 'application/json' } }),
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

async function type(value: string): Promise<void> {
  await act(async () => {
    fireEvent.click(screen.getByRole('button', { name: `type-${value}` }));
    await Promise.resolve();
  });
}

/** Чекає, доки запит піде з дороги й відповідь ляже в кеш. */
async function settled(count: number): Promise<void> {
  await waitFor(() => expect(patches).toHaveLength(count));
  await waitFor(() => expect(inFlightRowKeys(1, 202609).size).toBe(0));
}

function sent(index: number): number {
  return Number(patches[index]?.[0]?.cells[0]?.value);
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
  vi.mocked(showWarning).mockClear();
});

describe('DocumentGrid: Undo двох правок однієї комірки (X2-01)', () => {
  it('10 → 20 → 25: Undo повертає 20 (показане), потім 10 — без хибного попередження', async () => {
    mockServer();
    show();
    await screen.findByTestId('revogrid-stub');

    await type('20');
    await type('25');
    await press('s', 'KeyS');
    await settled(1);
    expect(sent(0)).toBe(25);

    await press('z', 'KeyZ');
    await settled(2);
    // ⛔ Мутація: прибрати `asShownStep` в `applyEditedValue` — тут піде 10.
    expect(sent(1)).toBe(20);

    await press('z', 'KeyZ');
    await settled(3);
    expect(sent(2)).toBe(10);
    expect(vi.mocked(showWarning)).not.toHaveBeenCalled();
  });

  it('V-01: 10 → 20 → 10 (не збережено) — Ctrl+Z повертає 20 без хибного попередження', async () => {
    mockServer();
    show();
    await screen.findByTestId('revogrid-stub');

    await type('20');
    await type('10');

    await press('z', 'KeyZ');

    // ⛔ Мутація: прибрати крок у гілці `V-01` — Undo кроку `{10→20}` бачить 10
    // замість 20 і показує `grid.undoChangedSince`, нічого не надіславши.
    await settled(1);
    expect(sent(0)).toBe(20);
    expect(vi.mocked(showWarning)).not.toHaveBeenCalled();
  });

  it('повторне введення того самого незбереженого значення не пише порожнього кроку', async () => {
    mockServer();
    show();
    await screen.findByTestId('revogrid-stub');

    await type('20');
    await type('20');
    await press('s', 'KeyS');
    await settled(1);

    await press('z', 'KeyZ');
    await settled(2);
    expect(sent(1)).toBe(10);

    // ⛔ Без відсіювання в історії лишився б ще крок `{10→20}`: другий Ctrl+Z
    // побачив би 10 замість 20 і хибно сказав «змінено після кроку».
    await press('z', 'KeyZ');
    await act(async () => {
      await Promise.resolve();
    });
    expect(patches).toHaveLength(2);
    expect(vi.mocked(showWarning)).not.toHaveBeenCalled();
  });
});
