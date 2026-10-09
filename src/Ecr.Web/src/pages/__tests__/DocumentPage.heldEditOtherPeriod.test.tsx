import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { completeCellNavigation, currentCellNavigation } from '@/features/grid/cellNavigation';
import { registerHeldEditLookup, revealFirstHeldEdit } from '@/features/grid/settleEdits';
import { DocumentPage } from '@/pages/DocumentPage';

/**
 * N3-06: утримана (відхилена сервером) правка ІНШОГО періоду блокує дії, а показати її
 * нікуди: розкривач мовчки виходив на `held.periodKey !== periodKey`. Тепер період
 * перемикається, а комірка показується, щойно таблиці цього періоду прочитано.
 */
vi.mock('@/features/grid/DocumentGrid', () => ({
  DocumentGrid: (props: { tableInstanceId: number }): JSX.Element => (
    <div data-testid={`grid-${String(props.tableInstanceId)}`} />
  ),
}));

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function table(tableDefId: number, tableInstanceId: number) {
  return {
    allowsDynamicRows: false,
    maxDynamicRows: null,
    sheetCode: 'GEN',
    sheetDefId: 1,
    sheetNameL10n: { values: { en: 'GEN' } },
    sheetOrdinal: 0,
    tableCode: `T${String(tableDefId)}`,
    tableDefId,
    tableInstanceId,
    tableNameL10n: { values: { en: `Table ${String(tableDefId)}` } },
    tableOrdinal: 0,
  };
}

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return jsonResponse({
          denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false,
          permissions: [], simulatedForUserId: null, userId: 1, userName: 'tester',
        });
      }

      if (url.includes('/validation')) return jsonResponse({ documentId: 1, periodKey: 202401, validated: false, messages: [] });
      if (url.includes('/tables')) return jsonResponse([table(10, 100), table(20, 200)]);

      if (url.includes('/api/v1/documents/1')) {
        return jsonResponse({
          businessKey: 'DOC-0001', createdAt: '2026-01-01T00:00:00Z', id: 1, nameL10n: { values: {} },
          projectId: 1, sheetCount: 1, sheetStates: { GEN: 'Draft' },
        });
      }

      return jsonResponse(null);
    }),
  );
}

let search = '';
function Probe(): null {
  search = useLocation().search;

  return null;
}

afterEach(() => {
  const pending = currentCellNavigation();
  if (pending !== null) completeCellNavigation(pending.seq);
  registerHeldEditLookup(() => null);
  vi.unstubAllGlobals();
});

describe('N3-06: утримана правка іншого періоду', () => {
  it('«Show» перемикає період і показує комірку, а не мовчить', async () => {
    mockFetch();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <MantineProvider>
        <MemoryRouter initialEntries={['/documents/1?periodKey=202401']}>
          <QueryClientProvider client={client}>
            <Probe />
            <Routes>
              <Route path="/documents/:id" element={<DocumentPage />} />
            </Routes>
          </QueryClientProvider>
        </MemoryRouter>
      </MantineProvider>,
    );

    registerHeldEditLookup(() => ({
      tableInstanceId: 200,
      periodKey: 202312,
      edit: { rowKey: 'R1', columnCode: 'C1', value: 'abc', isEmpty: false, baseVersion: 'v1' },
      message: 'Value must be a number',
    }));

    let revealed = false;
    await waitFor(() => {
      act(() => {
        revealed = revealFirstHeldEdit();
      });
      expect(revealed).toBe(true);
    });

    // Період перемкнуто на період утриманої правки...
    await waitFor(() => expect(search).toContain('periodKey=202312'));
    // ...і комірку показано (запит переходу адресує таблицю з нового періоду).
    await waitFor(() =>
      expect(currentCellNavigation()).toMatchObject({ tableInstanceId: 200, rowKey: 'R1', columnCode: 'C1' }),
    );
  });
});
