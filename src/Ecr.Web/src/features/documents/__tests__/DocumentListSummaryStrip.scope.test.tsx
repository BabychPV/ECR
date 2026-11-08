import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentListSummaryStrip, summaryScopeLabel } from '@/features/documents/DocumentListSummaryStrip';
import { formatPeriodKey } from '@/shared/format';
import { testTheme } from '@/test/render';

/**
 * ✎ UAT-p3 №3 (людський прохід W15k): огляд кампанії показував «92 Draft», перелік документів — «90 Draft · 2 Partly»,
 * і жоден не казав, що рахує. Смуга переліку тепер називає рік або місяць і проєкти (обраний чи всі видимі).
 */

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Documents counters name their period and project scope', () => {
  it('рік має перевагу над місяцем; без проєкту — всі видимі, з проєктом — обраний', () => {
    const year = summaryScopeLabel(202603, 2026, null);
    expect(year).toContain('documents.summaryScopeYear (year=2026)');
    expect(year).toContain('documents.summaryScopeAllProjects');

    const month = summaryScopeLabel(202603, null, 7);
    expect(month).toContain(`period=${formatPeriodKey(202603)}`);
    expect(month).not.toContain('summaryScopeYear');
    expect(month).toContain('documents.summaryScopeOneProject');
  });

  it('смуга показує підпис області поруч із лічильниками', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => json({ draft: 1, submitted: 0, approved: 0, rejected: 0, withIssues: 0 })),
    );
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <DocumentListSummaryStrip periodKey={null} year={2026} filters={{ state: null, setState: () => {} }} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    const group = await screen.findByRole('group', { name: '⟦documents.summaryLabel⟧' });
    const scope = group.querySelector('[data-summary-scope]');
    expect(scope?.textContent).toContain('documents.summaryScopeYear (year=2026)');
  });
});
