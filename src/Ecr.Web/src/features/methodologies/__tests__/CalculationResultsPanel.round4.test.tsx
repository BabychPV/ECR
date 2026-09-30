import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CalculationResultsPanel } from '@/features/methodologies/CalculationResultsPanel';
import { testTheme } from '@/test/render';

/**
 * Панель результатів методології — четвертий раунд UX (F-02, F-05, F-21).
 *
 * ⛔ Відтворено на стенді: після перерахунку панель не оновлювалася до
 * перезавантаження сторінки (жила під ключем, якого ніхто не інвалідує), після
 * зміни входів показувала старі числа як чинні, а значення мали 16 знаків і
 * версію — ідентифікатором.
 */

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function mockServer(results: () => unknown[]): { resultCalls: () => number } {
  let calls = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.endsWith('/api/v1/units')) {
        return json([{ id: 8, code: 't', nameL10n: { values: {} } }]);
      }

      if (path.endsWith('/calculation-results')) {
        calls += 1;
        return json(results());
      }

      return json(null);
    }),
  );

  return { resultCalls: () => calls };
}

function row(value: string, isStale = false): Record<string, unknown> {
  return {
    sourceRowKey: 'R1',
    outputCode: 'EMISSION',
    value,
    unitId: 8,
    substanceEntryId: null,
    methodologyVersionId: 5,
    methodologyCode: 'R4F_M1',
    methodologyVersion: '1.1.0',
    isStale,
    calculatedAt: '2026-09-24T22:18:58Z',
    inputsChangedAt: isStale ? '2026-09-24T23:00:00Z' : null,
  };
}

function show(): QueryClient {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <CalculationResultsPanel documentId={19} periodKey={202609} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return client;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CalculationResultsPanel: четвертий раунд UX', () => {
  /**
   * Мутація: повернути ключ `['calculation-results', …]` — інвалідація
   * документа не зачіпає панель, другого запиту немає.
   */
  it('інвалідація документа й періоду (завершений перерахунок) перечитує панель', async () => {
    let value = '20.0000000000000000';
    const server = mockServer(() => [row(value)]);
    const client = show();

    await screen.findByText('20');
    expect(server.resultCalls()).toBe(1);

    value = '25.0000000000000000';
    await client.invalidateQueries({ queryKey: ['document', 19, 202609] });

    await screen.findByText('25');
    expect(server.resultCalls()).toBe(2);
  });

  it('число без хвостових нулів, версія — кодом і номером, а не ідентифікатором (F-21)', async () => {
    mockServer(() => [row('2.5000000000000000')]);
    show();

    await screen.findByText('2.5');
    expect(screen.getByText('R4F_M1 1.1.0')).toBeTruthy();
    expect(screen.queryByText('2.5000000000000000')).toBeNull();
  });

  /** Мутація: прибрати банер `isStale` — застарілі числа показуються як чинні. */
  it('застарілі результати позначено банером (F-05)', async () => {
    mockServer(() => [row('20', true)]);
    show();

    await waitFor(() => expect(document.querySelector('[data-results-stale]')).not.toBeNull());
  });

  it('свіжі результати банера не мають', async () => {
    mockServer(() => [row('20', false)]);
    show();

    await screen.findByText('20');
    expect(document.querySelector('[data-results-stale]')).toBeNull();
  });
});
