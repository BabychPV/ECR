import type { JSX, ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useYearUnmatchedRows } from '@/features/documents/useUnmatchedRows';

/**
 * ✎ Огляд b11 #3 (п. 6): місяці за стелею записів року читаються окремими запитами (`useQueries`); результат хука —
 * ТОЙ САМИЙ об'єкт між рендерами, доки дані не змінились (інакше панелі перераховували причини на кожен рендер).
 */
const Anchor = 209901;
const YearMonths: readonly number[] = [209909, 209910];

const formulaError = (monthNumber: number) => ({
  tableCode: 'T0', sheetCopyId: null, rowKey: 'R1', rowOrdinal: 1, rowLabelL10n: null, columnCode: 'M01',
  columnHeaderL10n: null, kind: 'Error', errorCode: '#VALUE', formula: '[A] / [B]', inputs: [], source: null,
  since: '2099-11-03T08:00:00Z', dependentCount: 0, monthNumber,
});

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('useYearUnmatchedRows — стабільність результату', () => {
  it('після приходу місяців за стелею повторний рендер без нових даних дає той самий перелік місяців', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes('year=true')) return json({ items: [], formulaErrors: [formulaError(9)], formulaErrorTotal: 600 });
        if (url.includes('periodKey=209910')) return json({ items: [], formulaErrors: [formulaError(10)], formulaErrorTotal: 1 });
        return json({ items: [], formulaErrors: [formulaError(9)], formulaErrorTotal: 599 });
      }),
    );
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const wrapper = ({ children }: { children: ReactNode }): JSX.Element => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );

    const { result, rerender } = renderHook(() => useYearUnmatchedRows(1, Anchor, null, true, null, YearMonths), { wrapper });

    await waitFor(() => expect(result.current.months.map((m) => m.periodKey)).toEqual([209909, 209910]));
    const before = result.current.months;
    rerender();
    rerender();

    expect(result.current.months).toBe(before);
  });
});
