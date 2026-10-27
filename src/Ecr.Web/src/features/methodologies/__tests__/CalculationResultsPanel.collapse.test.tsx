import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CalculationResultsPanel, ResultsCollapsedKey } from '@/features/methodologies/CalculationResultsPanel';
import { testTheme } from '@/test/render';

/**
 * Блок «Calculation results» згортається (ui-sheet-focus, D): порожньо — згорнуто, є числа — розгорнуто,
 * явний вибір людини (localStorage) сильніший за авто.
 *
 * ⚠ У тестах каталог рядків порожній: текст UiString видно як ключ `⟦…⟧`.
 */
function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function row(code: string): Record<string, unknown> {
  return {
    sourceRowKey: 'R1',
    outputCode: code,
    value: '1',
    unitId: 8,
    substanceEntryId: null,
    methodologyVersionId: 5,
    methodologyCode: 'M',
    methodologyVersion: '1.0.0',
    isStale: false,
    calculatedAt: '2026-09-24T22:18:58Z',
    inputsChangedAt: null,
  };
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

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <CalculationResultsPanel documentId={1} periodKey={202610} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

beforeEach(() => {
  window.localStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
  window.localStorage.clear();
});

describe('CalculationResultsPanel: згортання', () => {
  it('немає результатів — згорнуто, у заголовку «немає», вміст не рендериться', async () => {
    mockServer([]);
    show();

    const toggle = await screen.findByTestId('calc-results-toggle');
    await screen.findByText(/calculationResultsNone/);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByText('⟦documents.noCalculationResults⟧')).toBeNull();
  });

  it('є результати — розгорнуто, у заголовку лічильник', async () => {
    mockServer([row('A'), row('B')]);
    show();

    await screen.findByText('A');
    expect(screen.getByTestId('calc-results-toggle').getAttribute('aria-expanded')).toBe('true');
    expect(screen.getByTestId('calc-results-count').textContent).toContain('2');
  });

  it('вибір людини пам’ятається і сильніший за авто', async () => {
    mockServer([row('A')]);
    show();

    const toggle = await screen.findByTestId('calc-results-toggle');
    await screen.findByText('A');
    fireEvent.click(toggle);

    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(window.localStorage.getItem(ResultsCollapsedKey)).toBe('1');
  });

  it('збережений вибір «розгорнуто» тримає блок відкритим і при порожньому результаті', async () => {
    window.localStorage.setItem(ResultsCollapsedKey, '0');
    mockServer([]);
    show();

    expect(await screen.findByText('⟦documents.noCalculationResults⟧')).toBeTruthy();
    expect(screen.getByTestId('calc-results-toggle').getAttribute('aria-expanded')).toBe('true');
  });
});
