import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CalculationResultsPanel } from '@/features/methodologies/CalculationResultsPanel';
import { RecalculateHintId, calculationResultsKey } from '@/features/methodologies/calculationResultsKey';
import { testTheme } from '@/test/render';

/**
 * Після імпорту: банер «застаріло» і тост «Перерахуйте». Щойно перерахунок завершено й
 * результати перечитано (свіжі), банер зникає, а тост знімається — не висить до перезавантаження.
 */

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function row(isStale: boolean): unknown {
  return {
    sourceRowKey: 'R1',
    outputCode: 'CO2',
    value: '20',
    unitId: 8,
    substanceEntryId: null,
    methodologyVersionId: 5,
    methodologyCode: 'HSE301',
    methodologyVersion: '1.0.0',
    isStale,
    calculatedAt: '2026-09-30T10:00:00Z',
    inputsChangedAt: isStale ? '2026-09-30T11:00:00Z' : null,
    changedRegistries: null,
  };
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('CalculationResultsPanel: скидання «застаріло» після перерахунку', () => {
  it('свіжі результати прибирають банер і знімають тост підказки імпорту', async () => {
    let stale = true;
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const path = String(input).split('?')[0] ?? '';
        if (path.endsWith('/api/v1/units')) return json([{ id: 8, code: 't', nameL10n: { values: {} } }]);
        if (path.endsWith('/calculation-results')) return json([row(stale)]);
        return json(null);
      }),
    );
    const hide = vi.spyOn(notifications, 'hide');
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <CalculationResultsPanel documentId={19} periodKey={202609} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    await waitFor(() => expect(document.querySelector('[data-results-stale]')).not.toBeNull());
    expect(hide).not.toHaveBeenCalled();

    // Завершення recalc-задачі: SheetActions інвалідує `['document', id, period]`.
    stale = false;
    await act(async () => {
      await client.invalidateQueries({ queryKey: calculationResultsKey(19, 202609).slice(0, 3) });
    });

    await waitFor(() => expect(document.querySelector('[data-results-stale]')).toBeNull());
    // ⛔ Мутація: прибрати `useEffect` з `notifications.hide` — тост лишається, тест червоний.
    expect(hide).toHaveBeenCalledWith(RecalculateHintId);
  });
});
