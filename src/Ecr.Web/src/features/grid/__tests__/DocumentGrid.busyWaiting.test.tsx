import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave, clearBusyRetry } from '../autosave';
import { pendingRejections, resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * AN-123 (`R1-03` = `R2-01`): `409 ECR-DOC-4091 lockTimeout` у сітці — НЕ відмова.
 *
 * ⛔ До виправлення: увесь пакет утримано (`V-01`), червоний банер «Нічого не
 * збережено», позначка «Not saved» і кнопка «Retry save» — ручна робота на стан,
 * що минає сам. Тепер — нейтральний стан «чекає» (`data-save-status="waiting"`),
 * без утримання й без кнопки повтору: автозбереження повторить саме.
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { onAfteredit?: (event: { detail: unknown }) => void }) => (
    <div data-testid="revogrid-stub">
      <button
        type="button"
        onClick={() =>
          props.onAfteredit?.({
            detail: { prop: 'C1', model: { __rowKey: 'r1' }, val: 'abc' },
          })
        }
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
      {
        cells: { C1: 321 },
        isOrphaned: false,
        label: null,
        ordinal: 0,
        rowKey: 'r1',
        rowKind: 'Item',
        rowVersion: 'v1',
      },
    ],
  };
}

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        return new Response(
          JSON.stringify({
            title: 'Зайнято',
            status: 409,
            detail: 'The data is busy with a long operation.',
            errorCode: 'ECR-DOC-4091',
            correlationId: 'c1',
            messageKey: 'err.ECR-DOC-4091.lockTimeout',
            sqlError: '1222',
          }),
          { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
        );
      }

      return new Response(JSON.stringify(sliceFixture()), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

function show(): HTMLElement {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  const { container } = render(
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

  return container;
}

afterEach(() => {
  cancelAutosave();
  clearBusyRetry();
  resetPending();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('DocumentGrid: 409 lockTimeout — стан «чекає», а не відмова (AN-123)', () => {
  it('нейтральний стан «чекає»; правка не утримана; немає банера, позначки й кнопки повтору', async () => {
    // Найдовший відступ першого повтору (5 с): стан «чекає» встигаємо побачити до повтору.
    vi.spyOn(Math, 'random').mockReturnValue(0.999);
    mockServer();
    const container = show();

    await screen.findByTestId('revogrid-stub');

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'simulate-edit' }));

    await waitFor(
      () => {
        expect(container.querySelector('[data-save-status="waiting"]')).not.toBeNull();
      },
      { timeout: 3_000 },
    );

    // ⛔ Мутація: прибрати гілку `isTransientBusy` у `holdRejectedEdits`/`rejectionMarksOf` —
    // правка утримана, з'являються позначка помилки й «Retry save».
    expect(pendingRejections(1, 202609).size).toBe(0);
    expect(container.querySelector('[data-save-status="error"]')).toBeNull();
    expect(screen.queryByTestId('grid-retry-save')).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
