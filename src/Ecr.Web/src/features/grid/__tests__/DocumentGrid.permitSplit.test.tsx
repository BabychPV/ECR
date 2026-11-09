import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cellSplitRequest, closeCellSplit } from '@/features/permits/history/splitRequest';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * Feature (j) j3: a single value typed into a month the row shares between two permits is refused
 * (`422 ECR-CELL-4225 splitRequired`, `cells[]`); the save banner keeps the reason and offers «Split <month>», which
 * opens the split of exactly that cell.
 *
 * Mutation: drop `setSplitRefusal(splitRefusalOf(error))` in `DocumentGrid.tsx` — no `save-error-split`, red.
 */
vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { onAfteredit?: (event: { detail: unknown }) => void }) => (
    <div data-testid="revogrid-stub">
      <button type="button" onClick={() => props.onAfteredit?.({ detail: { prop: 'Feb', model: { __rowKey: 'r1' }, val: '12.4' } })}>
        simulate-edit
      </button>
    </div>
  ),
}));

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202602,
    tableInstanceId: 1,
    columns: [
      {
        code: 'Feb',
        dataType: 'Decimal',
        defaultValue: null,
        displayFormat: null,
        header: 'February',
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
    rows: [{ cells: { Feb: null }, isOrphaned: false, label: 'Sea Water Intake', ordinal: 0, rowKey: 'r1', rowKind: 'Item', rowVersion: 'v1' }],
  } as unknown as TableSliceDto;
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  closeCellSplit();
  vi.unstubAllGlobals();
});

describe('DocumentGrid · ECR-CELL-4225 → «Split <month>» (j3)', () => {
  it('the refusal banner opens the split of the named cell', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_path: string, init?: RequestInit) =>
        init?.method === 'PATCH'
          ? new Response(
              JSON.stringify({
                title: 'The month must be split between the permits',
                status: 422,
                detail: 'Not saved: row r1 is split between two permits in 02.2026',
                errorCode: 'ECR-CELL-4225',
                correlationId: 'c1',
                messageKey: 'err.ECR-CELL-4225.splitRequired',
                rowKey: 'r1',
                month: '02.2026',
                permits: 'KZ29 / KZ46',
                cells: [{ rowKey: 'r1', columnCode: 'Feb' }],
              }),
              { status: 422, headers: { 'Content-Type': 'application/problem+json' } },
            )
          : new Response(JSON.stringify(sliceFixture()), { status: 200, headers: { 'Content-Type': 'application/json' } }),
      ),
    );
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <MantineProvider>
        <QueryClientProvider client={client}>
          <DocumentGrid
            documentId={7}
            tableInstanceId={1}
            tableDefId={701}
            periodKey={202602}
            readOnly={false}
            allowsDynamicRows={false}
            maxDynamicRows={null}
          />
        </QueryClientProvider>
      </MantineProvider>,
    );

    await screen.findByTestId('revogrid-stub');
    await userEvent.setup().click(screen.getByRole('button', { name: 'simulate-edit' }));

    const split = await waitFor(() => screen.getByTestId('save-error-split'), { timeout: 5_000 });
    await userEvent.setup().click(within(split).getByRole('button'));

    expect(cellSplitRequest()).toEqual({
      documentId: 7,
      periodKey: 202602,
      tableInstanceId: 1,
      tableDefId: 701,
      sheetCopyId: null,
      rowKey: 'r1',
      columnCode: 'Feb',
    });
  });
});
