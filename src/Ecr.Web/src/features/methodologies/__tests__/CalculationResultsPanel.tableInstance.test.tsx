import { afterEach, describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CalculationResultsPanel } from '@/features/methodologies/CalculationResultsPanel';
import { testTheme } from '@/test/render';

/**
 * D-401: результати двох таблиць з однаковим RowKey — два окремі рядки панелі з колонкою
 * «Таблиця» (екземпляр), а не один; NULL (результат до D-401) показується прочерком.
 */

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function result(value: string, tableInstanceId: number | null): Record<string, unknown> {
  return {
    sourceRowKey: 'M01',
    outputCode: 'EMISSION',
    value,
    unitId: 8,
    substanceEntryId: null,
    methodologyVersionId: 5,
    methodologyCode: 'R4F_M1',
    methodologyVersion: '1.1.0',
    isStale: false,
    calculatedAt: '2026-09-24T22:18:58Z',
    inputsChangedAt: null,
    tableInstanceId,
  };
}

function show(rows: unknown[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.endsWith('/api/v1/units')) {
        return json([{ id: 8, code: 't', nameL10n: { values: {} } }]);
      }

      return path.endsWith('/calculation-results') ? json(rows) : json(null);
    }),
  );

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <CalculationResultsPanel documentId={19} periodKey={202609} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('CalculationResultsPanel — екземпляр таблиці (D-401)', () => {
  // ✎ ui-sheet-bottom: блок за замовчуванням згорнуто — ці тести перевіряють ВМІСТ, тож розгортають його вибором людини.
beforeEach(() => {
  window.localStorage.setItem('ecr.calcResults.collapsed', '0');
});

afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('два результати з однаковим RowKey різних таблиць — два рядки зі своєю таблицею', async () => {
    show([result('10', 11), result('20', 12), result('5', null)]);

    await waitFor(() => expect(document.querySelectorAll('tbody tr')).toHaveLength(3));

    const cells = Array.from(document.querySelectorAll('td[data-result-table]')).map((c) => c.textContent);
    expect(cells).toEqual(['11', '12', '—']);
    expect(screen.getByText('10')).toBeTruthy();
    expect(screen.getByText('20')).toBeTruthy();
  });
});