import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave, scheduleAutosave } from '../autosave';
import { putPendingEdit, resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * Живий прохід 10.11: правка комірки, яку не можна писати (обчислювана колонка `SNAME = SHEETNAME()` свіжого
 * динамічного рядка), давала банер без колонки й рядка («Cells you may not edit in this batch: 1. Reason for the
 * first: CalculatedCell»). Тепер:
 *  1. `403 deniedCells` — банер називає «колонка · row N: причина» (текст причини, не назву enum) і не показує
 *     шістнадцятковий ключ рядка;
 *  2. правка обчислюваної комірки взагалі НЕ надсилається (`computedEditsOf`) — і банер так само її називає.
 *
 * Мутації: (1) прибрати `deniedCellReasons` у `DocumentGrid.save` — перелік зникає; (2) прибрати `computedEditsOf`
 * перед `withKnownVersions` — `PATCH` іде на сервер.
 */

const HexRow = 'abcdef0123456789abcdef0123456789';

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { onAfteredit?: (event: { detail: unknown }) => void }) => (
    <div data-testid="revogrid-stub">
      <button
        type="button"
        onClick={() => props.onAfteredit?.({ detail: { prop: 'C1', model: { __rowKey: HexRow }, val: 'x' } })}
      >
        simulate-edit
      </button>
    </div>
  ),
}));

function column(code: string, header: string, dataType: string, ordinal: number): Record<string, unknown> {
  return {
    code,
    dataType,
    defaultValue: null,
    displayFormat: null,
    header,
    id: ordinal + 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    lookupRegistryDefId: null,
    ordinal,
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
    columns: [column('C1', 'Колонка 1', 'Text', 0), column('SNAME', 'Sheet name', 'Formula', 1)],
    rows: [
      { cells: { C1: 'a', SNAME: null }, isOrphaned: false, label: null, ordinal: 0, rowKey: HexRow, rowKind: 'Item', rowVersion: 'v1' },
    ],
  } as unknown as TableSliceDto;
}

function mockServer(): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async (_path: string, init?: RequestInit) => {
    if (init?.method === 'PATCH') {
      return new Response(
        JSON.stringify({
          title: 'Forbidden',
          status: 403,
          detail: 'Cells you may not edit in this batch: 1. Reason for the first: CalculatedCell.',
          errorCode: 'ECR-ACCS-0403',
          correlationId: 'c1',
          messageKey: 'err.ECR-ACCS-0403.deniedCells',
          deniedCount: '1',
          reason: 'CalculatedCell',
        }),
        { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
      );
    }

    return new Response(JSON.stringify(sliceFixture()), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    });
  });
  vi.stubGlobal('fetch', fetchMock);

  return fetchMock;
}

function renderGrid(): void {
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
          allowsDynamicRows
          maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const patches = (fetchMock: ReturnType<typeof vi.fn>): number =>
  fetchMock.mock.calls.filter((call) => (call[1] as RequestInit | undefined)?.method === 'PATCH').length;

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('DocumentGrid · заборонена комірка називає колонку, рядок і причину (живий прохід 10.11)', () => {
  it('403 deniedCells — «Колонка 1 · row N: текст причини», без ключа рядка й назви enum', async () => {
    const fetchMock = mockServer();
    renderGrid();

    await screen.findByTestId('revogrid-stub');
    await userEvent.setup().click(screen.getByRole('button', { name: 'simulate-edit' }));

    const reasons = await waitFor(() => screen.getByTestId('save-error-reasons'), { timeout: 5_000 });
    const text = reasons.textContent ?? '';
    expect(text).toMatch(/^Колонка 1 · .*permits\.history\.rowPosition.*: .*deny\.CalculatedCell/);
    expect(text).not.toContain(HexRow);
    expect(patches(fetchMock)).toBe(1);

    const alerts = screen
      .getAllByRole('alert')
      .map((alert) => alert.textContent ?? '')
      .join('\n');
    expect(alerts).toContain('grid.deniedCellsSummary');
    expect(alerts).not.toContain('Reason for the first: CalculatedCell');
  });

  it('правка обчислюваної комірки (Formula) не надсилається, банер її називає', async () => {
    const fetchMock = mockServer();
    renderGrid();

    await screen.findByTestId('revogrid-stub');
    putPendingEdit(1, 202609, {
      rowKey: HexRow,
      columnCode: 'SNAME',
      value: 'typed',
      isEmpty: false,
      baseVersion: 'v1',
      before: null,
    });
    scheduleAutosave();

    const reasons = await waitFor(() => screen.getByTestId('save-error-reasons'), { timeout: 5_000 });
    expect(reasons.textContent ?? '').toMatch(/^Sheet name · .*permits\.history\.rowPosition.*: .*deny\.CalculatedCell/);
    expect(patches(fetchMock)).toBe(0);
  });
});
