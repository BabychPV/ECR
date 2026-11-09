import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { StructureChangesPanel } from '@/features/audit/StructureChangesPanel';
import { formatDateTime } from '@/shared/format';
import { siteMomentText } from '@/shared/siteMoment';
import { testTheme } from '@/test/render';

/**
 * ✎ debt-1109 F13 (P3 A): журнал «Reopen» — «до …» уже в часі майданчика (`zone` у конверті), а стовпець ЧАСУ
 * запису лишався в поясі браузера: 19:00Z у Токіо — «Nov 10» у причині й «Nov 9» у стовпці. Обидва — у часі сайту.
 *
 * Мутація (локально): не передавати `zone` у `Timestamp` панелі — перший тест червоніє.
 */
const ChangedAt = '2026-11-09T19:00:00Z';

function respond(reason: string | null): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const body = url.includes('/api/v1/audit/structure')
        ? {
            items: [
              {
                changedAt: ChangedAt,
                changedByUserId: 1,
                changedByDisplayName: 'Admin',
                entityType: 'Document',
                entityId: 7,
                entityLabel: null,
                operation: 'Reopen',
                changeReason: reason,
              },
            ],
            nextCursor: null,
          }
        : null;
      return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/audit?view=structure']}>
        <QueryClientProvider client={client}>
          <StructureChangesPanel from="2026-11-01" to="2026-11-30" />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('StructureChangesPanel: час запису Reopen — у часі майданчика', () => {
  it('конверт із zone — стовпець часу в поясі сайту, як і «до …»', async () => {
    respond(
      JSON.stringify({
        k: 'document.reopen.audit.reopened',
        p: { reason: 'R', until: '2026-11-12T19:00:00Z', zone: 'Asia/Tokyo' },
      }),
    );
    show();

    await screen.findByRole('table', {}, { timeout: 60_000 });
    const node = document.querySelector(`time[datetime="${ChangedAt}"]`);
    expect(node?.textContent).toBe(siteMomentText(ChangedAt, 'Asia/Tokyo'));
    // 19:00Z у Токіо — уже 10 листопада (середовище тестів — UTC).
    expect(node?.textContent).toMatch(/Nov 10, 2026/u);
  }, 90_000);

  it('дзеркало: запис без zone — пояс читача, як і раніше', async () => {
    respond('Manual reason');
    show();

    await screen.findByRole('table', {}, { timeout: 60_000 });
    const node = document.querySelector(`time[datetime="${ChangedAt}"]`);
    expect(node?.textContent).toBe(formatDateTime(ChangedAt));
  }, 90_000);
});
