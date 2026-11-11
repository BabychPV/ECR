import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { CalculationResultsPage } from '@/pages/CalculationResultsPage';
import { yearFixtureFor } from '@/features/calcResults/__tests__/fixture';
import { testTheme } from '@/test/render';

/**
 * Екран «Calculation results» (CALC-RESULTS.md): спершу зведення, рядки — лише розгорнутих таблиць (перша —
 * одразу), значення місяців, сума лише адитивних, пошук на сервері, шторка й перехід до джерела, а в річній розкладці
 * заголовок місяця обирає «місяць результатів», а не відкриває документ.
 * ⚠ Каталог рядків у тестах порожній: тексти видно як ключі `⟦…⟧`.
 * Мутація (локально): у `CalcResultsGrid.total` прибрати перевірку `unit.additive` — червоніє «неадитивний вихід».
 */
function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

const requests: string[] = [];

function serve(templateLayout: string | null): void {
  requests.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      requests.push(url);
      const year = yearFixtureFor(url);
      if (year !== null) return json(year);
      if (/\/api\/v1\/documents\/7$/.test(url)) return json({ id: 7, businessKey: 'DOC-7', templateLayout });
      return json(null);
    }),
  );
}

function Where(): JSX.Element {
  const location = useLocation();
  return <output data-testid="where">{`${location.pathname}${location.search}`}</output>;
}

function show(entry = '/documents/7/results?year=2026'): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[entry]}>
          <Routes>
            <Route path="/documents/:id/results" element={<><CalculationResultsPage /><Where /></>} />
            <Route path="/documents/:id" element={<Where />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const rowsRequests = (): string[] => requests.filter((u) => u.includes('/calculation-results/year/rows'));

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CalculationResultsPage', () => {
  it('зведення один раз, рядки лише першої таблиці; значення місяців, сума адитивного з n/12 і «—» для неадитивного', async () => {
    serve(null);
    show();
    const grid = await screen.findByTestId('calc-results-grid');
    await waitFor(() => expect(grid.querySelector('[data-cr-row="r-co2"]')).not.toBeNull());

    expect(requests.filter((u) => u.includes('/calculation-results/year/summary?year=2026'))).toHaveLength(1);
    expect(rowsRequests()).toHaveLength(1);
    expect(rowsRequests()[0]).toContain('table=10');
    expect(rowsRequests()[0]).toContain('offset=0');
    const co2 = grid.querySelector('[data-cr-row="r-co2"]')!;
    expect(within(co2 as HTMLElement).getByText('1.5')).toBeTruthy();
    expect(co2.textContent).toContain('2/12');
    const rate = grid.querySelector('[data-cr-row="r-rate"]')!;
    expect(rate.querySelector('.ecr-cr-tot--na')?.textContent).toBe('—');
    expect(grid.querySelectorAll('th[scope="col"]')).toHaveLength(15);

    // Друга таблиця згорнута — її рядків не просили; розгорнути — одна сторінка.
    expect(grid.querySelector('[data-cr-row="r-ghg"]')).toBeNull();
    fireEvent.click(grid.querySelector('[data-cr-group="20"]')!);
    await waitFor(() => expect(grid.querySelector('[data-cr-row="r-ghg"]')).not.toBeNull());
    expect(rowsRequests().filter((u) => u.includes('table=20'))).toHaveLength(1);
  });

  it('пошук іде на сервер (зведення й сторінка з q); шторка значення веде до документа на аркуші, таблиці й місяці', async () => {
    serve(null);
    show();
    await screen.findByTestId('calc-results-grid');

    fireEvent.change(screen.getByLabelText(/calcResults\.search/), { target: { value: 'ES-002' } });
    // Нова сторінка з q приходить після паузи пошуку: чекаємо саме її (є «r-flare», немає «r-co2»).
    await waitFor(
      () => {
        expect(document.querySelector('[data-cr-row="r-flare"]')).not.toBeNull();
        expect(document.querySelector('[data-cr-row="r-co2"]')).toBeNull();
      },
      { timeout: 3000 },
    );
    expect(requests.some((u) => u.includes('/year/summary') && u.includes('q=ES-002'))).toBe(true);
    expect(rowsRequests().some((u) => u.includes('q=ES-002'))).toBe(true);

    fireEvent.click(document.querySelector('[data-cr-cell="r-flare#2"]')!);
    const open = await screen.findByTestId('cr-open-source');
    fireEvent.click(open);
    await waitFor(() =>
      expect(screen.getByTestId('where').textContent).toBe('/documents/7?periodKey=202602&sheet=AIR&table=T1_2'),
    );
  });

  it('помісячні копії: заголовок місяця відкриває документ на цьому місяці', async () => {
    serve('PerMonthCopies');
    show();
    await screen.findByTestId('calc-results-grid');
    fireEvent.click(document.querySelector('[data-cr-month="3"]')!);
    await waitFor(() => expect(screen.getByTestId('where').textContent).toBe('/documents/7?periodKey=202603'));
  });

  it('річна розкладка: місяць результатів — останній із прогоном, клік на заголовку його змінює', async () => {
    serve('YearWithMonthColumns');
    show();
    await screen.findByTestId('cr-year-note');
    expect(document.querySelector('[data-cr-month="2"]')?.getAttribute('aria-pressed')).toBe('true');

    fireEvent.click(document.querySelector('[data-cr-month="1"]')!);
    await waitFor(() => expect(screen.getByTestId('where').textContent).toContain('month=1'));
    expect(document.querySelector('[data-cr-month="1"]')?.getAttribute('aria-pressed')).toBe('true');
  });
});
