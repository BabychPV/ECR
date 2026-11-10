import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SecurityEventsPanel } from '@/features/audit/SecurityEventsPanel';
import { formatDateTime } from '@/shared/format';
import { siteMomentText } from '@/shared/siteMoment';
import { testTheme } from '@/test/render';

/**
 * ✎ debt-1110: журнал безпеки — стовпець часу в поясі МАЙДАНЧИКА (`siteTimeZoneId` рядка), а не браузера, як уже
 * журнал «Reopen» (`StructureChangesPanel.siteZone.test.tsx`). 19:00Z у Токіо — уже 10 листопада.
 *
 * Мутація (локально): у панелі знову `Timestamp` без поясу — перший тест червоніє.
 */
const ChangedAt = '2026-11-09T19:00:00Z';

function respond(siteTimeZoneId: string | null): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const body = url.includes('/api/v1/audit/security')
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

describe('SecurityEventsPanel: час події — у часі майданчика', () => {
  it('рядок із siteTimeZoneId — стовпець часу в поясі сайту', async () => {
    respond('Asia/Tokyo');
    show();

    await screen.findByRole('table', {}, { timeout: 60_000 });
    const node = document.querySelector(`time[datetime="${ChangedAt}"]`);
    expect(node?.textContent).toBe(siteMomentText(ChangedAt, 'Asia/Tokyo'));
    // Середовище тестів — UTC: у поясі браузера це було б ще 9 листопада.
    expect(node?.textContent).toMatch(/Nov 10, 2026/u);
  }, 90_000);

  it('дзеркало: пояс невідомий (різні пояси проєктів) — пояс читача', async () => {
    respond(null);
    show();

    await screen.findByRole('table', {}, { timeout: 60_000 });
    const node = document.querySelector(`time[datetime="${ChangedAt}"]`);
    expect(node?.textContent).toBe(formatDateTime(ChangedAt));
  }, 90_000);
});
