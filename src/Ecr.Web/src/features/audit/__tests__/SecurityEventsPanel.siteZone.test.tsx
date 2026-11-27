import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SecurityEventsPanel } from '@/features/audit/SecurityEventsPanel';
import { testTheme } from '@/test/render';

/**
 * ✎ P-1124-4 (слово людини 24.11: «показуй у місцевому часі»): журнал безпеки — стовпець часу в поясі ЧИТАЧА,
 * 24-годинним записом; пояс майданчика (`siteTimeZoneId`) — лише в підказці поруч із точним ISO.
 * Замінює debt-1110 (час у поясі майданчика без позначки: дія о 18:12 за Києвом стояла як «9:12 PM»).
 *
 * 19:00Z у читача в UTC (CI) — «Nov 9, 2026, 19:00»; у Токіо (майданчик) — уже «Nov 10, 2026, 04:00».
 * Мутація (локально): у панелі знову `SiteChangedAt` / пояс майданчика — перший тест червоніє.
 */
const ChangedAt = '2026-11-09T19:00:00Z';

/** Місцевий час читача: пояс середовища тесту (CI — UTC, машина розробника — будь-який), 24 год, без AM/PM. */
const Local = new Intl.DateTimeFormat('en', { dateStyle: 'medium', timeStyle: 'short', hourCycle: 'h23' }).format(new Date(ChangedAt));

function respond(siteTimeZoneId: string | null): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const body = url.includes('/api/v1/audit/security/event-types')
        ? ['IdleSignOut']
        : url.includes('/api/v1/audit/authors')
          ? [{ userId: 1, displayName: 'Admin' }]
          : url.includes('/api/v1/audit/security')
            ? {
                items: [
                  {
                    changedAt: ChangedAt,
                    changedByUserId: 1,
                    changedByDisplayName: 'Admin',
                    eventType: 'IdleSignOut',
                    detailsJson: '{"reason":"idle"}',
                    targetUserId: null,
                    targetRoleId: null,
                    correlationId: null,
                    siteTimeZoneId,
                  },
                ],
                nextCursor: null,
                totalCount: null,
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
      <MemoryRouter initialEntries={['/admin/audit?view=security']}>
        <QueryClientProvider client={client}>
          <SecurityEventsPanel from="2026-11-01" to="2026-11-30" />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SecurityEventsPanel: час події — місцевий час читача', () => {
  it('рядок із siteTimeZoneId — видно місцевий час 24 год, час майданчика — у підказці', async () => {
    respond('Asia/Tokyo');
    show();

    await screen.findByRole('table', {}, { timeout: 60_000 });
    const node = document.querySelector(`time[datetime="${ChangedAt}"]`);
    expect(node?.textContent).toBe(Local);
    expect(node?.textContent).not.toMatch(/AM|PM/u);
    const title = node?.getAttribute('title') ?? '';
    expect(title).toContain(ChangedAt);
    expect(title).toContain('Tokyo');
    expect(title).toContain('Nov 10, 2026, 04:00');
  }, 90_000);

  it('дзеркало: пояс невідомий — той самий місцевий час, у підказці лише ISO', async () => {
    respond(null);
    show();

    await screen.findByRole('table', {}, { timeout: 60_000 });
    const node = document.querySelector(`time[datetime="${ChangedAt}"]`);
    expect(node?.textContent).toBe(Local);
    expect(node?.getAttribute('title')).toBe(ChangedAt);
  }, 90_000);
});
