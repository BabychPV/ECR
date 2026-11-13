import { afterEach, describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { CalculationResultsPanel } from '@/features/methodologies/CalculationResultsPanel';
import { testTheme } from '@/test/render';

/**
 * ✎ 12.11 (живий прохід координатора): блок під аркушами друкував ГОЛИЙ ідентифікатор екземпляра таблиці (56724…) у
 * колонці «Table» і числа трьох аркушів одним переліком. Перелік-дубль прибрано на користь огляду «усі місяці»
 * (`/documents/:id/results`, назви аркушів і таблиць по таблицях); блок дає лічильник і перехід туди.
 *
 * Мутація: повернути перелік з `tableInstanceId` — перший тест червоний (ідентифікатор і число на екрані).
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

function show(rows: unknown[], inRouter: boolean): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';
      return path.endsWith('/calculation-results') ? json(rows) : json(null);
    }),
  );

  const panel = (
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <CalculationResultsPanel documentId={19} periodKey={202609} />
    </QueryClientProvider>
  );
  render(<MantineProvider theme={testTheme}>{inRouter ? <MemoryRouter>{panel}</MemoryRouter> : panel}</MantineProvider>);
}

describe('CalculationResultsPanel — без переліку з ідентифікаторами таблиць (12.11)', () => {
  // Блок за замовчуванням згорнуто — тести перевіряють ВМІСТ, тож розгортають його вибором людини.
  beforeEach(() => {
    window.localStorage.setItem('ecr.calcResults.collapsed', '0');
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    window.localStorage.clear();
  });

  it('ні ідентифікатора екземпляра, ні переліку чисел — лише лічильник і перехід до огляду року з місяцем', async () => {
    show([result('10.5', 56724), result('20.25', 56725), result('5', null)], true);

    const link = await screen.findByTestId('calc-results-open-year');
    expect(link.getAttribute('href')).toBe('/documents/19/results?year=2026&month=9');
    expect(screen.getByTestId('calc-results-count').textContent).toBe('· 3');

    const body = document.getElementById('ecr-calc-results-body')?.textContent ?? '';
    expect(body).not.toContain('56724');
    expect(body).not.toContain('10.5');
    expect(document.querySelector('table')).toBeNull();
  });

  it('поза роутером (ізольована панель) — звичайне посилання на ту саму адресу', async () => {
    show([result('1', 56724)], false);

    const link = await screen.findByTestId('calc-results-open-year');
    expect(link.tagName.toLowerCase()).toBe('a');
    expect(link.getAttribute('href')).toBe('/documents/19/results?year=2026&month=9');
  });
});
