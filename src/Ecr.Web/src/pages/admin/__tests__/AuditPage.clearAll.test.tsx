import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { AuditPage } from '@/pages/admin/AuditPage';
import { testTheme } from '@/test/render';

/**
 * audit-filters-live AC-9 (рішення людини 26.11): «Clear filters» знімає ВСЕ разом — документ, дати (типове вікно),
 * Row key, Column, Origin, By user, Late — і так само на кожній вкладці журналу.
 * ⛔ Червоний на 7afb19c97: кнопка лишала документ і дати, а на вкладках Structure/Security/Workflow її не було.
 */
const SlowEnvTimeout = 120_000;
const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

let location = '';
function Where(): JSX.Element | null {
  location = useLocation().search;
  return null;
}

function show(search: string): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), 'http://x');
      if (url.pathname === '/api/v1/me') {
        return json({ userId: 1, userName: 'a', language: 'en', permissions: ['Security.ViewAudit'] });
      }
      if (url.pathname === '/api/v1/audit/authors') return json([]);
      return json({ items: [], nextCursor: null, totalCount: 0 });
    }),
  );
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MantineProvider theme={testTheme}>
        <MemoryRouter initialEntries={[`/admin/audit?${search}`]}>
          <AuditPage />
          <Where />
        </MemoryRouter>
      </MantineProvider>
    </QueryClientProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('AuditPage: «Clear filters» знімає все (AC-9)', () => {
  it(
    'Cell changes: документ, дати, адреса комірки, походження, автор і Late — разом',
    async () => {
      show('from=2026-11-01&to=2026-11-10&documentId=96&rowKey=Methane&columnDefId=73809&author=3&origin=UserEdit&lateOnly=true');
      const user = userEvent.setup();

      await user.click(await screen.findByRole('button', { name: '⟦audit.reset⟧' }));
      await waitFor(() => expect(location).toBe(''));
    },
    SlowEnvTimeout,
  );

  it.each([
    ['structure', 'entityType=TableDef&changedBy=3'],
    ['security', 'eventType=LoginFailed&changedBy=3'],
    ['workflow', 'action=Approve&changedBy=3'],
  ])(
    'вкладка %s: кнопка є і знімає фільтри вкладки й дати, вкладка лишається',
    async (view, filters) => {
      show(`view=${view}&from=2026-11-01&to=2026-11-10&${filters}`);
      const user = userEvent.setup();

      await user.click(await screen.findByRole('button', { name: '⟦audit.reset⟧' }));
      await waitFor(() => expect(location).toBe(`?view=${view}`));
    },
    SlowEnvTimeout,
  );
});
