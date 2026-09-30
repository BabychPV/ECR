import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { resetOutOfWindowMarks } from '../outOfWindowMarks';
import { DocumentGrid } from '../DocumentGrid';

/**
 * `ФВ-2.16`, `D-239`: значок «правка поза вікном» переживає перезавантаження.
 *
 * ⛔ Сценарій — рівно F5: жодного `PATCH` у цій вкладці не було, модуль
 * `outOfWindowMarks.ts` порожній, і значок може прийти лише зі зрізу
 * (`TableSliceDto.outOfWindowCells`). Доти сітка брала його тільки з відповіді
 * `PATCH` і після перезаходу показувала комірку як звичайну.
 *
 * RevoGrid підмінено заглушкою, що запам'ятовує колонки: перевіряються
 * атрибути комірки, які сітка віддає веб-компоненту.
 */

type Column = {
  prop?: unknown;
  cellProperties?: (props: unknown) => Record<string, unknown>;
};

let columns: Column[] = [];

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { columns?: Column[] }) => {
    columns = props.columns ?? [];
    return <div data-testid="revogrid-stub" />;
  },
}));

function sliceFixture(outOfWindowCells?: string[] | null): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202601,
    tableInstanceId: 1,
    columns: [
      {
        code: 'C1',
        dataType: 'Decimal',
        defaultValue: null,
        displayFormat: null,
        header: 'Колонка 1',
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
      { cells: { C1: 2 }, isOrphaned: false, label: null, ordinal: 1, rowKey: 'r2', rowKind: 'Item', rowVersion: 'v1' },
    ],
    ...(outOfWindowCells === undefined ? {} : { outOfWindowCells }),
  };
}

function show(slice: TableSliceDto): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(JSON.stringify(slice), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    ),
  );

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentGrid
          documentId={1}
          tableInstanceId={1}
          tableDefId={1}
          periodKey={202601}
          readOnly={false}
          allowsDynamicRows={false}
          maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function cellProps(rowKey: string): Record<string, unknown> {
  const column = columns.find((c) => c.prop === 'C1');
  if (column?.cellProperties === undefined) {
    throw new Error('Сітка ще не отримала колонку C1');
  }

  return column.cellProperties({ model: { __rowKey: rowKey }, prop: 'C1' });
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  resetOutOfWindowMarks();
  vi.unstubAllGlobals();
  columns = [];
});

describe('DocumentGrid: значок поза вікном після перезавантаження (ФВ-2.16)', () => {
  it('комірка з TableSliceDto.outOfWindowCells позначена без жодного PATCH', async () => {
    show(sliceFixture(['r2:C1']));

    // ⛔ Мутація «не передавати slice.data?.outOfWindowCells у
    // useOutOfWindowMarks» (стан до цього кроку) лишає r2 без значка.
    await waitFor(() => expect(cellProps('r2')['data-out-of-window']).toBe('true'));
    expect(String(cellProps('r2')['class'])).toContain('ecr-cell-out-of-window');

    expect(cellProps('r1')['data-out-of-window']).toBeUndefined();
  });

  it('старіший сервер без поля — сітка працює, значків немає', async () => {
    show(sliceFixture(undefined));

    await waitFor(() => expect(columns.length).toBeGreaterThan(0));
    expect(cellProps('r1')['data-out-of-window']).toBeUndefined();
    expect(cellProps('r2')['data-out-of-window']).toBeUndefined();
  });
});
