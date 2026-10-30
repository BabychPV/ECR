import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { DocumentTableDto } from '@/api/types';
import { testTheme } from '@/test/render';
import { DocumentInspector } from '@/features/documents/inspector/DocumentInspector';
import { clearInspectedCell, publishInspectedCell, type InspectedCell } from '@/features/documents/inspector/inspectedCell';

/**
 * ✎ D-461: історія комірки в інспекторі показує причину ручного значення замість формули — словами під
 * походженням; системний літерал «Excel import» — ключем каталогу; рядок без причини — без рядка причини.
 */

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

const change = {
  changedByUserId: 5,
  changedByDisplayName: 'M. Petrenko',
  columnDefId: 33,
  documentId: 1,
  isLateEdit: false,
  isOutOfWindow: false,
  periodKey: 202609,
  rowKey: 'R4',
};

const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
  const url = String(input);

  if (url.includes('/api/v1/audit/cells')) {
    return jsonResponse({
      items: [
        { ...change, changedAt: '2026-10-30T10:00:00Z', oldValue: '10', newValue: '12', origin: 'FormulaOverride', reason: 'Meter replaced' },
        { ...change, changedAt: '2026-10-30T09:00:00Z', oldValue: '9', newValue: '10', origin: 'FormulaOverride', reason: 'Excel import' },
        { ...change, changedAt: '2026-10-30T08:00:00Z', oldValue: null, newValue: '9', origin: 'Recalculation', reason: null },
      ],
      nextCursor: null,
    });
  }

  if (url.includes('/activity')) return jsonResponse({ items: [], nextCursor: null });
  if (url.includes('/tables/status')) return jsonResponse([]);

  return new Response(null, { status: 404 });
});

const Tables = [
  {
    allowsDynamicRows: false,
    maxDynamicRows: null,
    sheetCode: 'A',
    sheetDefId: 1,
    sheetNameL10n: { values: { en: 'Sheet A' } },
    sheetOrdinal: 0,
    tableCode: 'T1',
    tableDefId: 1,
    tableInstanceId: 10,
    tableNameL10n: { values: { en: 'Fuel' } },
    tableOrdinal: 0,
  } as DocumentTableDto,
];

const Cell: InspectedCell = {
  tableInstanceId: 10,
  periodKey: 202609,
  rowKey: 'R4',
  rowLabel: 'Diesel',
  columnCode: 'C3',
  columnDefId: 33,
  columnHeader: 'Volume',
  unitSymbol: 'm³',
  dataType: 'Decimal',
  scale: 4,
  isCalculated: true,
  isReadOnly: false,
  value: '12',
};

afterEach(() => {
  cleanup();
  act(() => clearInspectedCell());
  fetchMock.mockClear();
  vi.unstubAllGlobals();
});

describe('інспектор: історія комірки з ручним значенням замість формули (D-461)', () => {
  it('причина — під походженням; «Excel import» — словами каталогу; без причини — нічого', async () => {
    vi.stubGlobal('fetch', fetchMock);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <MemoryRouter initialEntries={['/documents/1?panel=history']}>
            <DocumentInspector
              documentId={1}
              periodKey={202609}
              tables={Tables}
              messages={null}
              onSelectFinding={() => {}}
              sheet={{ sheetDefId: 101, code: 'A', name: 'Sheet A' }}
            />
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>,
    );

    act(() => publishInspectedCell(Cell));
    const cellRadio = (await screen.findByRole('radio', { name: '⟦inspector.history.modeCell⟧' })) as HTMLInputElement;
    await waitFor(() => expect(cellRadio.disabled).toBe(false));
    fireEvent.click(cellRadio);

    await waitFor(() => expect(document.querySelectorAll('[data-inspector-history] [role="listitem"]')).toHaveLength(3));
    const reasons = [...document.querySelectorAll('[data-inspector-reason]')].map((node) => node.textContent);

    expect(reasons).toEqual(['⟦audit.reason⟧: Meter replaced', '⟦audit.reason⟧: ⟦audit.reasonExcelImport⟧']);
  });
});
