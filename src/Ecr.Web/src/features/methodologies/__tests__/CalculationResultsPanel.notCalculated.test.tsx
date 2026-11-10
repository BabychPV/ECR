import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CalculationResultsPanel } from '@/features/methodologies/CalculationResultsPanel';
import { testTheme } from '@/test/render';

/**
 * ✎ 10.11 (doc 95, P1-V9-0093): рядок T3_00 відхилило правило категорії, і панель казала лише
 * «Calculation results · none» — без причини й без переходу. Тепер заголовок рахує рядки, які розрахунок не взяв, а
 * тіло веде до банера над сіткою, де кожен рядок відкриває комірку, яку треба заповнити.
 *
 * Мутація: прибрати `notCalculated` із заголовка — червоніє перший тест; прибрати фокус банера — другий.
 */

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

const rejected = {
  detectedAt: '2026-11-10T16:36:00Z',
  kind: 'CategoryRuleRejected',
  methodologyCode: 'ECW_C05_01',
  rowKey: '2ac8045f',
  rowNumber: 1,
  sheetCopyId: null,
  tableCode: 'T3_00',
  ruleCode: 'R_C05_01_01',
  columns: [{ code: 'Land_OverhauledEnginesRef', nameL10n: { values: { en: 'Overhauled engines' } } }],
};

function show(items: unknown[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';
      if (path.endsWith('/calculation-issues')) return json({ items });
      if (path.endsWith('/calculation-results')) return json([]);

      return json([]);
    }),
  );

  render(
    <MantineProvider theme={testTheme}>
      {/* Банер над сіткою (`UnmatchedRowsBanner`) — лише його `data-testid` і перемикач, на який панель переводить фокус. */}
      <div data-testid="document-unmatched-rows">
        <button type="button" aria-controls="document-unmatched-rows-body" data-testid="banner-toggle">
          show
        </button>
      </div>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <CalculationResultsPanel documentId={95} periodKey={202610} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('CalculationResultsPanel — рядки, які розрахунок не взяв (10.11)', () => {
  beforeEach(() => {
    window.localStorage.setItem('ecr.calcResults.collapsed', '0');
    Element.prototype.scrollIntoView = vi.fn();
  });
  afterEach(() => {
    window.localStorage.clear();
    vi.unstubAllGlobals();
  });

  it('«none» більше не мовчить: заголовок називає кількість рядків, не взятих у розрахунок', async () => {
    show([rejected, { ...rejected, methodologyCode: 'ECW_C05_02' }]);

    // Один рядок, відхилений двома методологіями, — один рядок.
    await waitFor(() =>
      expect(screen.getByTestId('calc-results-not-calculated-count').textContent).toBe(
        '· ⟦documents.calculationResultsNotCalculated (count=1)⟧',
      ),
    );
    expect(screen.getByTestId('calc-results-count').textContent).toBe('· ⟦documents.calculationResultsNone⟧');
    expect(screen.getByTestId('calc-results-not-calculated').textContent).toContain(
      '⟦documents.calculationResultsNotCalculatedHint (count=1)⟧',
    );
  });

  it('посилання веде до банера над сіткою: прокрутка й фокус на його перемикачі', async () => {
    show([rejected]);

    await userEvent.click(await screen.findByTestId('calc-results-not-calculated-link'));

    expect(document.activeElement).toBe(screen.getByTestId('banner-toggle'));
  });

  it('без проблем розрахунку нічого зайвого не з’являється', async () => {
    show([]);

    await waitFor(() => expect(screen.getByTestId('calc-results-count')).toBeTruthy());
    expect(screen.queryByTestId('calc-results-not-calculated-count')).toBeNull();
    expect(screen.queryByTestId('calc-results-not-calculated')).toBeNull();
  });
});
