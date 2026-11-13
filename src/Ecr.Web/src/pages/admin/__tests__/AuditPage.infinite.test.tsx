import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuditPage } from '@/pages/admin/AuditPage';
import { testTheme } from '@/test/render';

/**
 * W-3b (рішення PO 11.11): «Load more» журналу змін комірок ДОПИСУЄ сторінку, а не замінює її; відмова другої
 * сторінки не ховає першу.
 * ⛔ Червоний без фіксу: після кліку в таблиці лишався лише рядок другої сторінки (курсор жив у ключі запиту).
 */
const SlowEnvTimeout = 400_000;
const Base = 'from=2026-01-01&to=2026-01-31';

function row(author: string, doc: string): Record<string, unknown> {
  return {
    changedAt: '2026-01-05T10:00:00Z',
    periodKey: 202601,
    documentId: 7,
    rowKey: 'R1',
    columnDefId: 3,
    oldValue: '1',
    newValue: '2',
    changedByUserId: 41,
    origin: 'UserEdit',
    isLateEdit: false,
    isOutOfWindow: false,
    isOverwrite: false,
    fromArchive: false,
    changedByDisplayName: author,
    documentBusinessKey: doc,
    columnCode: 'C3',
  };
}

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

function serve(failSecond: boolean): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), 'http://x');

      if (url.pathname === '/api/v1/me') {
        return json({ userId: 1, userName: 'a', language: 'en', permissions: ['Security.ViewAudit'] });
      }
      if (url.pathname === '/api/v1/audit/cells') {
        if (url.searchParams.get('cursor') === null) {
          return json({ items: [row('Ann', 'DOC-7')], nextCursor: 'c2', totalCount: 2 });
        }
        if (failSecond) return json({ status: 500, title: 'Server error', errorCode: 'ECR-SYS-0500' }, 500);

        return json({ items: [row('Bob', 'DOC-8')], nextCursor: null, totalCount: null });
      }

      return json(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[`/admin/audit?${Base}`]}>
        <QueryClientProvider client={client}>
          <AuditPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const bodyRows = (): number => document.querySelectorAll('table.ecr-audit-table tbody tr').length;

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('AuditPage: журнал комірок — нескінченна прокрутка (W-3b)', () => {
  it(
    'W-3b: Load more дописує сторінку журналу комірок',
    async () => {
      serve(false);
      show();
      const user = userEvent.setup();

      await screen.findByText('Ann', {}, { timeout: SlowEnvTimeout });
      expect(bodyRows()).toBe(1);

      await user.click(screen.getByRole('button', { name: '⟦documents.more⟧' }));

      await screen.findByText('Bob', {}, { timeout: SlowEnvTimeout });
      expect(bodyRows()).toBe(2);
      expect(screen.getByText('Ann')).toBeTruthy();
      await waitFor(() =>
        expect(document.querySelector('[data-infinite-status]')?.textContent).toContain('⟦list.allShown'),
      );
    },
    SlowEnvTimeout,
  );

  it(
    'W-3b: відмова другої сторінки — рядок першої лишається, є «Повторити»',
    async () => {
      serve(true);
      show();
      const user = userEvent.setup();

      await screen.findByText('Ann', {}, { timeout: SlowEnvTimeout });
      await user.click(screen.getByRole('button', { name: '⟦documents.more⟧' }));

      await screen.findByRole('button', { name: '⟦common.retry⟧' }, { timeout: SlowEnvTimeout });
      expect(screen.getByText('Ann')).toBeTruthy();
      expect(bodyRows()).toBe(1);
    },
    SlowEnvTimeout,
  );
});
