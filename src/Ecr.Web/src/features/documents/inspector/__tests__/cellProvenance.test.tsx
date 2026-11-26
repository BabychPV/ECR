import type { JSX, ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { provenanceFrom, provenanceLines, setProvenanceSource, useCellProvenance, type ProvenanceCell } from '../cellProvenance';

/**
 * ✎ DOCUX (доповнення координатора 26.11): Info інспектора — «Calculated by: <методологія> · step <крок>» для обчисленої
 * комірки, «Read by: <методології>» (з «required») для вхідної. Дані — з контракту сервера через типізований адаптер.
 */
const Cell: ProvenanceCell = { documentId: 7, tableInstanceId: 11, periodKey: 202609, rowKey: 'r1', columnCode: 'CO2' };

function wrapper({ children }: { readonly children: ReactNode }): JSX.Element {
  return <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>{children}</QueryClientProvider>;
}

afterEach(() => setProvenanceSource(null));

describe('cellProvenance', () => {
  it('обчислена комірка — методологія (назва, без неї — код) і крок', () => {
    const lines = provenanceLines(provenanceFrom({ calculatedBy: { methodologyCode: 'ECW_C05', methodologyName: 'Fuel combustion', stepCode: 'S3' } }));
    expect(lines).toHaveLength(1);
    expect(lines[0]?.label).toContain('inspector.info.calculatedBy');
    expect(lines[0]?.value).toContain('methodology=Fuel combustion');
    expect(lines[0]?.value).toContain('step=S3');
    expect(provenanceLines(provenanceFrom({ calculatedBy: { methodologyCode: 'ECW_C05', stepCode: null } }))[0]?.value).toBe('ECW_C05');
  });

  it('вхідна комірка — усі методології, що її читають; обов’язкова — з позначкою «required»', () => {
    const lines = provenanceLines(
      provenanceFrom({ readBy: [{ methodologyCode: 'A', methodologyName: 'Boilers', required: true }, { methodologyCode: 'B', methodologyName: '' }] }),
    );
    expect(lines[0]?.label).toContain('inspector.info.readBy');
    expect(lines[0]?.value).toContain('inspector.info.readByRequired (methodology=Boilers)');
    expect(lines[0]?.value).toContain(', B');
  });

  it('нічого сказати — рядків немає', () => {
    expect(provenanceLines(provenanceFrom(null))).toEqual([]);
    expect(provenanceLines(provenanceFrom({ readBy: [] }))).toEqual([]);
  });

  it('без джерела (контракт ще не зведено) — запиту немає; з джерелом — читає комірку інспектора', async () => {
    const none = renderHook(() => useCellProvenance(Cell), { wrapper });
    expect(none.result.current).toBeNull();

    const fetcher = vi.fn(async () => ({ calculatedBy: { methodologyCode: 'M', stepCode: '2' } }));
    setProvenanceSource(fetcher);
    const some = renderHook(() => useCellProvenance(Cell), { wrapper });
    await waitFor(() => expect(some.result.current).toEqual({ kind: 'calculated', methodology: 'M', step: '2' }));
    expect(fetcher).toHaveBeenCalledWith(Cell);
  });
});
