import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ConsistencyIssuesPage } from '@/pages/admin/ConsistencyIssuesPage';

/**
 * ✎ 24.11 (аудит «Consistency issues»): екран каже, ЩО перевірено, а не лише «0 знахідок».
 *
 * ⛔ Мутації (RED → GREEN, вручну): прибрати `<ChecksPerformed>` зі сторінки — червоніють перший і другий тести;
 * повернути `emptyTitle={t('consistency.empty')}` — червоніє «порожній стан називає кількість перевірок»; прибрати
 * `<FindingLinks>` з рядка — червоніє тест посилань.
 */
const Checks = [
  { ruleCode: 'ORPHANED_CELL', found: 0, complete: true, skipped: false },
  { ruleCode: 'CALC_RESULT_VERSION_NOT_EFFECTIVE', found: 2, complete: true, skipped: false },
  { ruleCode: 'NON_CURRENT_RUN_ROWS', found: 0, complete: false, skipped: true },
];

const Summary = {
  info: 0,
  warnings: 0,
  errors: 0,
  total: 0,
  lastDetectedAt: null,
  lastRun: { startedAt: '2026-11-24T16:12:44Z', finishedAt: '2026-11-24T16:12:56Z', status: 'Succeeded', checks: Checks },
};

function stub(issues: readonly unknown[], summary: unknown = Summary): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const body = url.endsWith('/api/v1/me')
        ? { permissions: [] }
        : url.includes('/consistency/summary')
          ? summary
          : url.includes('/consistency/issues')
            ? { items: issues, nextCursor: null, totalCount: issues.length, totals: { info: 0, warnings: issues.length, errors: 0 } }
            : { items: [], nextCursor: null };
      return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider>
      <MemoryRouter initialEntries={['/admin/consistency']}>
        <QueryClientProvider client={client}>
          <ConsistencyIssuesPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

function requested(): string[] {
  return (globalThis.fetch as unknown as { mock: { calls: unknown[][] } }).mock.calls.map((call) => String(call[0]));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ConsistencyIssuesPage: перевірки останнього прогону', () => {
  it('блок називає кожне правило людською назвою з кількістю знахідок і пропуском', async () => {
    stub([]);
    show();

    const block = await screen.findByRole('region', { name: '⟦consistency.checks.title (count=3)⟧' });
    const rows = within(block).getAllByRole('listitem');
    expect(rows.map((row) => row.getAttribute('data-rule'))).toEqual(Checks.map((c) => c.ruleCode));
    expect(within(rows[0]!).getByText('⟦consistency.rule.ORPHANED_CELL⟧')).toBeTruthy();
    expect(within(rows[1]!).getByLabelText('⟦consistency.checks.found (count=2)⟧').textContent).toBe('2');
    expect(within(rows[2]!).getByText('⟦consistency.checks.skipped⟧')).toBeTruthy();
  });

  it('клік по правилу фільтрує журнал за його кодом', async () => {
    stub([]);
    show();

    const block = await screen.findByRole('region', { name: '⟦consistency.checks.title (count=3)⟧' });
    await userEvent.click(within(block).getByRole('button', { name: /CALC_RESULT_VERSION_NOT_EFFECTIVE/ }));

    await vi.waitFor(() =>
      expect(requested().some((url) => url.includes('ruleCode=CALC_RESULT_VERSION_NOT_EFFECTIVE'))).toBe(true),
    );
    expect(within(block).getByRole('button', { pressed: true })).toBeTruthy();
  });

  it('порожній стан називає кількість виконаних перевірок, а не лише «замечаний нет»', async () => {
    stub([]);
    show();

    expect(await screen.findByText('⟦consistency.emptyChecked (count=3)⟧')).toBeTruthy();
    expect(screen.queryByText('⟦consistency.empty⟧')).toBeNull();
  });

  it('без завершеного прогону блоку немає, а порожній стан — колишній', async () => {
    stub([], { ...Summary, lastRun: null });
    show();

    expect(await screen.findByText('⟦consistency.empty⟧')).toBeTruthy();
    expect(screen.queryByRole('region', { name: /consistency\.checks\.title/ })).toBeNull();
  });

  it('рядок знахідки веде на документ за період і на методологію', async () => {
    const message = JSON.stringify({
      k: 'consistency.finding.CALC_RESULT_VERSION_NOT_EFFECTIVE',
      p: { documentId: '96', period: '202609', methodology: 'ECW_C11_03', methodologyId: '17', resultVersion: 'V2', effectiveVersion: 'V3', results: '18', runId: '10106' },
    });
    stub([
      { id: 5, detectedAt: '2026-11-24T16:12:56Z', severity: 2, ruleCode: 'CALC_RESULT_VERSION_NOT_EFFECTIVE', entityType: 'calc.CalculationResult', entityId: 901, message, resolvedAt: null, resolvedByUserId: null },
    ]);
    show();

    const document = await screen.findByRole('link', { name: '⟦consistency.openDocument (documentId=96, period=202609)⟧' });
    expect(document.getAttribute('href')).toBe('/documents/96?periodKey=202609');
    const methodology = screen.getByRole('link', { name: '⟦consistency.openMethodology (methodology=ECW_C11_03)⟧' });
    expect(methodology.getAttribute('href')).toBe('/admin/methodologies/17/versions');
    // Назва правила — і в рядку знахідки, і в блоці перевірок.
    expect(screen.getAllByText('⟦consistency.rule.CALC_RESULT_VERSION_NOT_EFFECTIVE⟧').length).toBeGreaterThanOrEqual(2);
  });
});
