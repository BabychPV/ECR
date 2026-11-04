import type { JSX, ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { calculationIssuesKey } from '../api';
import { useCalculationIssues } from '../useCalculationIssues';

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  apiFetch: vi.fn(),
}));

/**
 * Запит проблем розрахунку (AN-69): `sheetCopyId` іде в адресу лише для копії, оригінал його не шле; ключ кешу
 * розрізняє оригінал і копії, і лежить під префіксом документа-періоду (його скидає перерахунок).
 *
 * Мутаційний доказ (виконано, відновлено): у `getCalculationIssues` слати `sheetCopyId` завжди (`null` -> `"null"`)
 * валить «оригінал без sheetCopyId»; прибрати `query.set('sheetCopyId', …)` валить «копія шле sheetCopyId»;
 * ключ без `sheetCopyId` валить «різні ключі».
 */
let client: QueryClient;
const wrapper = ({ children }: { children: ReactNode }): JSX.Element => (
  <QueryClientProvider client={client}>{children}</QueryClientProvider>
);

beforeEach(() => {
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  vi.mocked(apiFetch).mockResolvedValue({ items: [] });
});

afterEach(() => {
  vi.clearAllMocks();
});

describe('useCalculationIssues', () => {
  it('копія шле sheetCopyId', async () => {
    const { result } = renderHook(() => useCalculationIssues(55, 202607, 9), { wrapper });
    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(apiFetch).toHaveBeenCalledWith('/api/v1/documents/55/calculation-issues?periodKey=202607&sheetCopyId=9');
  });

  it('оригінал без sheetCopyId', async () => {
    const { result } = renderHook(() => useCalculationIssues(55, 202607, null), { wrapper });
    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(apiFetch).toHaveBeenCalledWith('/api/v1/documents/55/calculation-issues?periodKey=202607');
  });

  it('різні ключі для оригіналу, копії, періоду й документа; префікс документа-періоду', () => {
    const keys = [
      calculationIssuesKey(55, 202607, null),
      calculationIssuesKey(55, 202607, 9),
      calculationIssuesKey(55, 202608, null),
      calculationIssuesKey(56, 202607, null),
    ].map((key) => JSON.stringify(key));

    expect(new Set(keys).size).toBe(4);
    expect(calculationIssuesKey(55, 202607, 9).slice(0, 3)).toEqual(['document', 55, 202607]);
  });
});
