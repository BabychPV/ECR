import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { pendingSlice, resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * `X2-03`: правки зі вставки й Undo/Redo не несли позначки `text` (`G1-06`), яку ставить
 * лише `captureEdit`. Для текстової колонки (`0012` — код, а не число 12) підтвердження
 * «це надіслано» (`wasSent`) тоді зводило `0012` і `12` до одного числа: нове введення
 * `0012` поверх надісланого `12` вважалося вже збереженим і мовчки зникало.
 *
 * ⚠ PATCH не відповідає ніколи — правка лишається в сховищі, предмет перевірки саме вона.
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
        onClick={() => props.onAfteredit?.({ detail: { prop: 'C1', model: { __rowKey: 'r1' }, val: '0012' } })}
      >
        type-0012
      </button>
    </div>
  ),
}));

function column(): ColumnDto {
  return {
    code: 'C1',
    dataType: 'String',
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

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [column()],
    rows: [
      {
        cells: { C1: 'A-1' },
        isOrphaned: false,
        label: null,
        ordinal: 0,
        rowKey: 'r1',
        rowVersion: 'v1',
        rowKind: 'Item' as const,
      },
    ],
  };
}

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn((_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') return new Promise<Response>(() => undefined);

      return Promise.resolve(
        new Response(JSON.stringify(sliceFixture()), { status: 200, headers: { 'Content-Type': 'application/json' } }),
      );
    }),
  );
}

function show(): void {
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
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('DocumentGrid: позначка text на правках вставки й Undo (X2-03)', () => {
  it('вставка в текстову колонку → правка у сховищі з text: true', async () => {
    mockServer();
    show();
    await screen.findByTestId('revogrid-stub');

    act(() => {
      fireEvent.paste(screen.getByTestId('revogrid-stub'), { clipboardData: { getData: () => '0012' } });
    });

    // ⛔ Мутація: прибрати `text` у вставці — тут буде `undefined`.
    expect(pendingSlice(1, 202609).get('r1:C1')?.text).toBe(true);
  });

  it('Undo в текстовій колонці → правка у сховищі з text: true', async () => {
    mockServer();
    show();
    await screen.findByTestId('revogrid-stub');

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'type-0012' }));
      await Promise.resolve();
    });
    expect(pendingSlice(1, 202609).get('r1:C1')?.value).toBe('0012');

    await act(async () => {
      fireEvent.keyDown(screen.getByTestId('revogrid-stub'), { key: 'z', code: 'KeyZ', ctrlKey: true });
      await Promise.resolve();
    });

    const undone = pendingSlice(1, 202609).get('r1:C1');
    expect(undone?.value).toBe('A-1');
    // ⛔ Мутація: прибрати `text` в `applyHistory` — тут буде `undefined`.
    expect(undone?.text).toBe(true);
  });
});
