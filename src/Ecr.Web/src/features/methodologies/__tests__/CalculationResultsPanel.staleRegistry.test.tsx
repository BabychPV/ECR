import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CalculationResultsPanel, changedRegistriesOf } from '@/features/methodologies/CalculationResultsPanel';
import type { CalculationResultDto } from '@/api/types';
import { testTheme } from '@/test/render';

/**
 * Банер застарілості через правку довідника (RT-25, FEATURE-REGISTRY-TABLES §5.10 п.2).
 *
 * ⛔ Причина застарілості — не лише введення: методологія читає довідник, і правка довідника після
 * прогону робить числа недійсними так само. Без назви довідника людина шукала б зміну в документі.
 */

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function mockServer(results: unknown[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.endsWith('/api/v1/units')) return json([{ id: 8, code: 't', nameL10n: { values: {} } }]);
      if (path.endsWith('/calculation-results')) return json(results);

      return json(null);
    }),
  );
}

function row(outputCode: string, changedRegistries: string[] | null, isStale = changedRegistries !== null): CalculationResultDto {
  return {
    sourceRowKey: 'R1',
    outputCode,
    value: '20',
    unitId: 8,
    substanceEntryId: null,
    methodologyVersionId: 5,
    methodologyCode: 'HSE301',
    methodologyVersion: '1.0.0',
    isStale,
    calculatedAt: '2026-09-30T10:00:00Z',
    inputsChangedAt: isStale ? '2026-09-30T11:00:00Z' : null,
    changedRegistries,
  };
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

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

describe('CalculationResultsPanel: довідник змінено після розрахунку (RT-25)', () => {
  /** Мутація: прибрати рядки `data-results-stale-registry` — банер мовчить, який довідник винен. */
  it('банер називає кожен змінений довідник рівно раз, хоч рядків чисел кілька', async () => {
    mockServer([row('CO2', ['GAS_COMPOSITION', 'COMPONENT']), row('CH4', ['GAS_COMPOSITION', 'COMPONENT'])]);
    show();

    await waitFor(() => expect(document.querySelectorAll('[data-results-stale-registry]')).toHaveLength(2));
    const names = [...document.querySelectorAll('[data-results-stale-registry]')].map((node) =>
      node.getAttribute('data-results-stale-registry'),
    );
    expect(names).toEqual(['COMPONENT', 'GAS_COMPOSITION']);
    expect(document.querySelector('[data-results-stale-registry="COMPONENT"]')?.textContent).toContain(
      'calculation.staleRegistry',
    );
  });

  /** Мутація: умова банера лише `isStale` — довідник змінено, а числа показуються як чинні. */
  it('змінений довідник показує банер, навіть коли сервер не підняв isStale', async () => {
    mockServer([row('CO2', ['COMPONENT'], false)]);
    show();

    await waitFor(() => expect(document.querySelector('[data-results-stale]')).not.toBeNull());
  });

  it('застаріле введенням без довідників — банер без рядків довідника', async () => {
    mockServer([row('CO2', [], true)]);
    show();

    await waitFor(() => expect(document.querySelector('[data-results-stale]')).not.toBeNull());
    expect(document.querySelector('[data-results-stale-registry]')).toBeNull();
  });

  it('старий сервер без поля — нічого не ламається', () => {
    const legacy = { ...row('CO2', null, false) } as Partial<CalculationResultDto>;
    delete legacy.changedRegistries;

    expect(changedRegistriesOf([legacy as CalculationResultDto])).toEqual([]);
  });
});
