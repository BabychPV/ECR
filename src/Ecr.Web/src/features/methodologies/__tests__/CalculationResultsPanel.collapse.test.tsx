import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CalculationResultsPanel, ResultsCollapsedKey } from '@/features/methodologies/CalculationResultsPanel';
import { calculationResultsKey } from '@/features/methodologies/calculationResultsKey';
import { testTheme } from '@/test/render';

/**
 * Блок «Calculation results» (ui-sheet-bottom): ЗАВЖДИ згорнуто за замовчуванням, запит чисел — ЛІНИВИЙ (лише
 * після розгортання; на Land до 174 тис. результатів на документ), явний вибір людини пам'ятається.
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

/** Скільки запитів чисел пішло (лічильник викликів fetch до `…/calculation-results`). */
let resultCalls = 0;

function mockServer(results: unknown[]): void {
  resultCalls = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';
      if (path.endsWith('/api/v1/units')) return json([{ id: 8, code: 't', nameL10n: { values: {} } }]);
      if (path.endsWith('/calculation-results')) {
        resultCalls += 1;
        return json(results);
      }
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

describe('CalculationResultsPanel: згортання і ліниве завантаження', () => {
  it('згорнуто за замовчуванням: запиту чисел НЕМАЄ, у заголовку немає лічильника', async () => {
    mockServer([row('A'), row('B')]);
    show();

    const toggle = await screen.findByTestId('calc-results-toggle');
    // Дати черзі запитів час, якби запит усе ж стартував.
    await new Promise((resolve) => setTimeout(resolve, 150));

    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(resultCalls).toBe(0);
    expect(screen.queryByTestId('calc-results-count')).toBeNull();
    expect(screen.queryByText('A')).toBeNull();
  });

  it('розгортання — рівно один запит; лічильник береться з відповіді; вибір пам’ятається', async () => {
    mockServer([row('A'), row('B')]);
    show();

    const toggle = await screen.findByTestId('calc-results-toggle');
    fireEvent.click(toggle);

    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(await screen.findByText('A')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByTestId('calc-results-count').textContent).toContain('2');
    });
    expect(resultCalls).toBe(1);
    expect(window.localStorage.getItem(ResultsCollapsedKey)).toBe('0');
  });

  it('поки числа вантажаться — тіло позначено aria-busy', async () => {
    window.localStorage.setItem(ResultsCollapsedKey, '0');
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => undefined)));
    show();

    const toggle = await screen.findByTestId('calc-results-toggle');
    const body = document.getElementById(toggle.getAttribute('aria-controls') ?? '') as HTMLElement;
    expect(body.getAttribute('aria-busy')).toBe('true');
  });

  it('порожній результат: після розгортання лічильник «немає»', async () => {
    mockServer([]);
    show();

    fireEvent.click(await screen.findByTestId('calc-results-toggle'));
    expect(await screen.findByText(/calculationResultsNone/)).toBeTruthy();
    expect(resultCalls).toBe(1);
  });

  it('дані вже в кеші (бейдж у шапці тримає той самий ключ) — згорнутий заголовок усе одно без числа', async () => {
    mockServer([]);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    client.setQueryData(calculationResultsKey(1, 202610), [row('A'), row('B')]);
    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <CalculationResultsPanel documentId={1} periodKey={202610} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    await screen.findByTestId('calc-results-toggle');
    expect(screen.queryByTestId('calc-results-count')).toBeNull();
    expect(resultCalls).toBe(0);
  });
  it('збережений вибір «згорнуто» пам’ятається і запиту немає', async () => {
    window.localStorage.setItem(ResultsCollapsedKey, '1');
    mockServer([row('A')]);
    show();

    const toggle = await screen.findByTestId('calc-results-toggle');
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(resultCalls).toBe(0);
  });

  it('збережений вибір «розгорнуто» одразу вантажить числа (один запит)', async () => {
    window.localStorage.setItem(ResultsCollapsedKey, '0');
    mockServer([row('A')]);
    show();

    expect(await screen.findByText('A')).toBeTruthy();
    expect(resultCalls).toBe(1);
  });
});
