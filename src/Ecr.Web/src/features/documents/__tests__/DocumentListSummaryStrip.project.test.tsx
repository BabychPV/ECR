import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentListSummaryStrip } from '@/features/documents/DocumentListSummaryStrip';
import { testTheme } from '@/test/render';

/**
 * Y7-02: смуга лічильників над переліком рахує за ТИМ САМИМ проєктом, що й таблиця під нею.
 *
 * ⛔ Мутаційний доказ: прибери `projectId` з `documentListSummary` — у запиті немає `projectId=5`, тест червоніє.
 */
const Summary = { draft: 2, submitted: 0, approved: 1, rejected: 0, withIssues: 0 };
const summaries: string[] = [];

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  summaries.length = 0;
});

function show(projectId: number | null): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      summaries.push(String(input));
      return new Response(JSON.stringify(Summary), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <DocumentListSummaryStrip periodKey={202601} filters={{ state: null, setState: vi.fn(), projectId }} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('DocumentListSummaryStrip: фільтр проєкту (Y7-02)', () => {
  it('обраний проєкт іде в запит зведення', async () => {
    show(5);

    await screen.findByRole('group', { name: '⟦documents.summaryLabel⟧' });

    expect(summaries).toHaveLength(1);
    const query = new URLSearchParams(summaries[0]?.split('?')[1] ?? '');
    expect(query.get('periodKey')).toBe('202601');
    expect(query.get('projectId')).toBe('5');
  });

  it('без проєкту параметра немає', async () => {
    show(null);

    await screen.findByRole('group', { name: '⟦documents.summaryLabel⟧' });

    expect(new URLSearchParams(summaries[0]?.split('?')[1] ?? '').has('projectId')).toBe(false);
  });
});
