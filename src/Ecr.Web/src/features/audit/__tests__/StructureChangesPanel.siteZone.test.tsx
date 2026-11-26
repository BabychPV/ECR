import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { StructureChangesPanel } from '@/features/audit/StructureChangesPanel';
import { testTheme } from '@/test/render';

/**
 * ✎ P-1124-4 (слово людини 24.11: «показуй у місцевому часі»): журнал структурних змін і «Reopen» — стовпець часу
 * в поясі ЧИТАЧА, 24-годинним записом. Пояс майданчика з конверта (`zone`) — у підказці до часу; строк «до …»
 * лишається часом майданчика (це строк, а не момент запису) і несе назву поясу, щоб два часи в рядку не плутались.
 * Замінює debt-1109 F13 (час запису в поясі майданчика без позначки).
 *
 * Мутація (локально): у панелі знову `SiteChangedAt` — перший тест червоніє.
 */
const ChangedAt = '2026-11-09T19:00:00Z';

/** Місцевий час читача: пояс середовища тесту (CI — UTC, машина розробника — будь-який), 24 год, без AM/PM. */
const Local = new Intl.DateTimeFormat('en', { dateStyle: 'medium', timeStyle: 'short', hourCycle: 'h23' }).format(new Date(ChangedAt));

function respond(reason: string | null): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const body = url.includes('/api/v1/audit/authors')
        ? [{ userId: 1, displayName: 'Admin' }]
        : url.includes('/api/v1/audit/structure')
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

describe('StructureChangesPanel: час запису — місцевий час читача', () => {
  it('конверт Reopen із zone — місцевий час 24 год; майданчик — у підказці; строк «до …» з назвою поясу', async () => {
    respond(
      JSON.stringify({
        k: 'document.reopen.audit.reopened',
        p: { reason: 'R', until: '2026-11-12T19:00:00Z', zone: 'Asia/Tokyo' },
      }),
    );
    show();

    const table = await screen.findByRole('table', {}, { timeout: 60_000 });
    const node = document.querySelector(`time[datetime="${ChangedAt}"]`);
    expect(node?.textContent).toBe(Local);
    expect(node?.getAttribute('title')).toContain('Tokyo');
    expect(node?.getAttribute('title')).toContain('Nov 10, 2026, 04:00');
    expect(table.textContent).toContain('(Tokyo)');
  }, 90_000);

  it('дзеркало: запис без zone — той самий місцевий час, у підказці лише ISO', async () => {
    respond('Manual reason');
    show();

    await screen.findByRole('table', {}, { timeout: 60_000 });
    const node = document.querySelector(`time[datetime="${ChangedAt}"]`);
    expect(node?.textContent).toBe(Local);
    expect(node?.getAttribute('title')).toBe(ChangedAt);
  }, 90_000);
});
