import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SecurityEventsPanel } from '@/features/audit/SecurityEventsPanel';
import { jobKindLabel } from '@/features/workflow/jobLabel';
import { testTheme } from '@/test/render';

/**
 * ✎ P-1124-4 (слово людини 24.11: «перевір фільтри щоб вони дійсно працювали»): «Event type» був вільним текстом,
 * а сервер шукає ТОЧНИЙ код (`EventType = @eventType`) — набране «Access denied» давало порожній журнал. Тепер це
 * вибір із типів, що є у вікні (`/audit/security/event-types`), підписом мовою інтерфейсу; у запит іде код.
 *
 * Мутація (локально): повернути `TextInput` — тест червоніє (немає варіанта «Access denied» у переліку).
 */
function respond(seen: string[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      seen.push(url);
      const body = url.includes('/api/v1/audit/security/event-types')
        ? ['AccessDenied', 'LoginFailed']
        : url.includes('/api/v1/audit/security')
          ? {
              items: [
                {
                  changedAt: '2026-11-09T19:00:00Z',
                  changedByUserId: 0,
                  changedByDisplayName: null,
                  eventType: 'JobCancelled',
                  detailsJson: '{"jobId":"IConsistencyCheckJob-9e3d9be36c654521a03734c933b50263","reason":"oji"}',
                  targetUserId: null,
                  targetRoleId: null,
                  correlationId: null,
                  siteTimeZoneId: null,
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

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SecurityEventsPanel: фільтр «Event type» — вибір, а не вільний текст', () => {
  it('варіанти — типи з вікна підписами; обраний іде в запит кодом; Details без сирого jobId', async () => {
    const seen: string[] = [];
    respond(seen);
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

    await screen.findByRole('table', {}, { timeout: 60_000 });
    expect(document.querySelector('[data-audit-details]')?.textContent).toBe(
      `job: ${jobKindLabel('IConsistencyCheckJob')} · reason: oji`,
    );
    expect(document.querySelector('td [data-audit-technical]')?.textContent).toBe(
      'IConsistencyCheckJob-9e3d9be36c654521a03734c933b50263',
    );

    const user = userEvent.setup();
    // Поле — за рядом фільтрів журналу (тест каталогу не вантажить, тож підпис — позначка ключа).
    const field = document.querySelector<HTMLInputElement>('[data-audit-filter-row="security"] input');
    expect(field).not.toBeNull();
    expect(field?.readOnly === true || field?.getAttribute('aria-haspopup') === 'listbox').toBe(true);
    await user.click(field!);
    await user.click(await screen.findByRole('option', { name: 'Access denied' }, { timeout: 30_000 }));

    await waitFor(
      () => {
        const journal = seen.filter((url) => url.includes('/api/v1/audit/security?')).at(-1) ?? '';
        expect(new URLSearchParams(journal.split('?')[1]).get('eventType')).toBe('AccessDenied');
      },
      { timeout: 30_000 },
    );
  }, 120_000);
});
