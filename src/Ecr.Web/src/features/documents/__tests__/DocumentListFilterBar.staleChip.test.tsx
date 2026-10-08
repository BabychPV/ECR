import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentListFilterBar } from '@/features/documents/DocumentListFilterBar';
import { useDocumentListFilters } from '@/features/documents/documentListFilters';
import { testTheme } from '@/test/render';

/**
 * RC14-D: чіп «Needs recalculation (N)». Лічильник зведення живе в серверному кеші й може відставати від
 * позначок у рядках, тож число в чіпі не менше за кількість застарілих рядків ПОТОЧНОЇ сторінки.
 *
 * ⛔ Мутаційний доказ: заміни `Math.max(summaryStale, staleOnPage)` на `summaryStale` — тест «зведення
 * відстає» почервоніє.
 */
const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

function mockSummary(staleResultsCount: number): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      json({ draft: 0, submitted: 0, approved: 0, rejected: 0, withIssues: 0, staleResultsCount }),
    ),
  );
}

function Bar({ staleOnPage }: { staleOnPage: number }): JSX.Element {
  const filters = useDocumentListFilters(202601);
  return <DocumentListFilterBar periodKey={202601} filters={filters} staleOnPage={staleOnPage} />;
}

function show(staleOnPage: number): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/?periodKey=202601']}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Bar staleOnPage={staleOnPage} />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const chipLabel = (): string =>
  document.querySelector('[data-stale-filter]')?.parentElement?.textContent ?? '';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentListFilterBar: чіп «Needs recalculation»', () => {
  it('зведення відстає (1) від рядків сторінки (3) - у чіпі 3', async () => {
    mockSummary(1);
    show(3);
    await waitFor(() => expect(chipLabel()).toMatch(/filterStale(?!NoCount)/), { timeout: 45_000 });
    expect(chipLabel()).toMatch(/\b3\b/);
  }, 60_000);

  it('зведення більше за рядки сторінки - у чіпі число зведення', async () => {
    mockSummary(9);
    show(2);
    await waitFor(() => expect(chipLabel()).toMatch(/filterStale(?!NoCount)/), { timeout: 45_000 });
    expect(chipLabel()).toMatch(/\b9\b/);
    expect(chipLabel()).not.toMatch(/\b2\b/);
  }, 60_000);
});
