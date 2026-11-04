import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave } from '@/features/grid/autosave';
import { resetPending } from '@/features/grid/pendingStore';
import { DocumentGrid } from '@/features/grid/DocumentGrid';
import { manualIndexOf } from '@/features/grid/formulaOverride';

/**
 * ✎ D-461: «Лише рядки з ручними значеннями» — лічильник ручних комірок, фільтр рядків сітки (джерело RevoGrid),
 * вимкнення повертає всі рядки; без `formulaOverride` перемикача немає.
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { source?: readonly { __rowKey?: string }[] }) => (
    <div data-testid="revogrid-stub" data-rows={String(props.source?.length ?? 0)} />
  ),
}));

function column(code: string, dataType: string, id: number): Record<string, unknown> {
  return {
    code,
    dataType,
    defaultValue: null,
    displayFormat: null,
    header: code,
    id,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    lookupRegistryDefId: null,
    ordinal: id,
    unitId: null,
    unitSymbol: null,
  };
}

function sliceFixture(withOverride: boolean): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202610,
    tableInstanceId: 1,
    columns: [column('A', 'Decimal', 1), column('F1', 'Formula', 2), column('F2', 'Formula', 3)],
    rows: ['r1', 'r2', 'r3', 'r4', 'r5'].map((rowKey, ordinal) => ({
      cells: { A: '1', F1: '2', F2: '3' },
      isOrphaned: false,
      label: null,
      ordinal,
      rowKey,
      rowKind: 'Item',
      rowVersion: 'v1',
    })),
    formulaOverride: withOverride
      ? {
          canOverride: true,
          overridableColumns: ['F1'],
          overridableCells: [],
          manualCells: {
            'r4:F1': { reason: 'a', setBy: 'Admin', setAt: '2026-10-30T10:00:00Z' },
            'r5:F1': { reason: 'b', setBy: 'Admin', setAt: '2026-10-30T10:00:00Z' },
            'r5:F2': { reason: 'c', setBy: 'Admin', setAt: '2026-10-30T10:00:00Z' },
          },
        }
      : null,
  } as unknown as TableSliceDto;
}

function show(withOverride: boolean): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(
      async () =>
        new Response(JSON.stringify(sliceFixture(withOverride)), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
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
          periodKey={202610}
          readOnly={false}
          allowsDynamicRows
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

describe('manualIndexOf', () => {
  it('ручні комірки в порядку сітки й рядки, що їх містять; без formulaOverride — порожньо', () => {
    const index = manualIndexOf(sliceFixture(true));
    expect(index.cells).toEqual([
      { rowKey: 'r4', columnCode: 'F1' },
      { rowKey: 'r5', columnCode: 'F1' },
      { rowKey: 'r5', columnCode: 'F2' },
    ]);
    expect([...index.rowKeys]).toEqual(['r4', 'r5']);
    expect(manualIndexOf(sliceFixture(false)).cells).toEqual([]);
  });
});

describe('DocumentGrid · «Лише рядки з ручними значеннями» (D-461)', () => {
  it('лічильник ручних комірок; увімкнено — лише рядки 4–5; вимкнено — усі', async () => {
    show(true);

    const toggle = (await screen.findByText('⟦grid.formulaOverride.filterManual⟧')).closest('button');
    expect(toggle).not.toBeNull();
    expect(document.querySelector('[data-manual-count]')?.getAttribute('data-manual-count')).toBe('3');
    await waitFor(() => expect(screen.getByTestId('revogrid-stub').getAttribute('data-rows')).toBe('5'));

    fireEvent.click(toggle as HTMLButtonElement);
    await waitFor(() => expect(screen.getByTestId('revogrid-stub').getAttribute('data-rows')).toBe('2'));
    expect(toggle?.getAttribute('aria-pressed')).toBe('true');

    fireEvent.click(toggle as HTMLButtonElement);
    await waitFor(() => expect(screen.getByTestId('revogrid-stub').getAttribute('data-rows')).toBe('5'));
    expect(document.querySelector('[data-manual-next]')).not.toBeNull();
  });

  it('таблиця без формул колонки/комірки — перемикача немає', async () => {
    show(false);
    await screen.findByTestId('revogrid-stub');
    expect(document.querySelector('[data-manual-only-toggle]')).toBeNull();
  });
});
