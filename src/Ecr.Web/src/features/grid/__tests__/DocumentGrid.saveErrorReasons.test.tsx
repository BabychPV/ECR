import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * D2 (приймальна Land): відмова збереження 422 з `cells[]` - у банері з'являється «Колонка · Рядок: текст правила».
 *
 * Мутація: прибрати виклик `setSaveErrorReasons(rejectedCellReasons(error))` у `DocumentGrid.tsx` - тест червоніє
 * (банер лишається лише з загальним текстом).
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { onAfteredit?: (event: { detail: unknown }) => void }) => (
    <div data-testid="revogrid-stub">
      <button
        type="button"
        onClick={() => props.onAfteredit?.({ detail: { prop: 'C1', model: { __rowKey: 'r1' }, val: '900' } })}
      >
        simulate-edit
      </button>
    </div>
  ),
}));

const Rule = 'Value must be <= 672';

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
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
      { cells: { C1: 5 }, isOrphaned: false, label: null, ordinal: 0, rowKey: 'r1', rowKind: 'Item', rowVersion: 'v1' },
    ],
  } as unknown as TableSliceDto;
}

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        return new Response(
          JSON.stringify({
            title: 'Validation rejected the save',
            status: 422,
            detail: 'Validation rejected the save: 1 cell(s) with an error',
            errorCode: 'ECR-CELL-0422',
            correlationId: 'c1',
            messageKey: 'err.ECR-CELL-0422.validationBlocked',
            cellCount: '1',
            cells: [{ rowKey: 'r1', columnCode: 'C1', ruleCode: 'DV_MAX', message: Rule }],
          }),
          { status: 422, headers: { 'Content-Type': 'application/problem+json' } },
        );
      }

      return new Response(JSON.stringify(sliceFixture()), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('DocumentGrid · причина відмови DV у банері (D2)', () => {
  it('422 з cells[] - «Колонка · Рядок: текст правила» у save-error-reasons', async () => {
    mockServer();
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

    await screen.findByTestId('revogrid-stub');
    await userEvent.setup().click(screen.getByRole('button', { name: 'simulate-edit' }));

    const reasons = await waitFor(() => screen.getByTestId('save-error-reasons'), { timeout: 5_000 });
    expect(reasons.textContent ?? '').toMatch(/^Колонка 1 · .+: Value must be <= 672$/);
  });
});