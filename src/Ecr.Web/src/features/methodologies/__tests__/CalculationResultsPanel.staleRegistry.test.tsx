import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CalculationResultsPanel } from '@/features/methodologies/CalculationResultsPanel';
import { testTheme } from '@/test/render';

/** RT-25 (ФВ-9.19): банер «змінено довідник …» у панелі результатів. */

function row(changed: string[] | null): Record<string, unknown> {
  return {
    sourceRowKey: 'R1',
    outputCode: 'EMISSION',
    value: '1',
    unitId: 8,
    substanceEntryId: null,
    methodologyVersionId: 5,
    methodologyCode: 'M1',
    methodologyVersion: '1.0.0',
    isStale: false,
    calculatedAt: '2026-09-24T22:18:58Z',
    inputsChangedAt: null,
    changedRegistries: changed,
  };
}

function show(rows: unknown[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';
      const body = path.endsWith('/calculation-results')
        ? rows
        : [{ id: 8, code: 't', nameL10n: { values: {} } }];

      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <CalculationResultsPanel documentId={19} periodKey={202609} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CalculationResultsPanel: змінений довідник', () => {
  /** Мутація: прибрати блок `changedRegistries` — банера немає. */
  it('показує банер, якщо довідник змінено після прогону', async () => {
    show([row(['SUBST']), row(['SUBST', 'UNITS'])]);

    const banner = await screen.findByText(/calculation\.staleRegistry/);
    expect(banner.closest('[data-results-stale-registry]')).not.toBeNull();
  });

  it('без змінених довідників банера немає', async () => {
    show([row(null), row([])]);

    await screen.findAllByText('EMISSION');
    expect(document.querySelector('[data-results-stale-registry]')).toBeNull();
  });
});
