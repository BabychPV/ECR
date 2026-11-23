import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * ✎ F5-R (D-570, рішення людини P-1122-4: «так познач щоб користувач бачив це»): рядок переліку документів, чий
 * затверджений місяць має змінене після затвердження джерело (`sourceChangedPeriods`), несе позначку «Source changed» з
 * підказкою (місяці й дія). Без поля — позначки немає.
 *
 * Мутація: прибрати `SourceChangedMark` із рядка `DocumentsPage` — червоний перший тест.
 */
const SlowEnvTimeout = 400_000;

const item = (id: number, sourceChangedPeriods: number[] | null) => ({
  id,
  businessKey: `DOC-00000${String(id)}`,
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 1,
  sheetStates: { S1: 'Approved' },
  hasLateEdits: false,
  sourceChangedPeriods,
});

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/api/v1/me')) {
        return json({ denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false, permissions: [], simulatedForUserId: null, userId: 1, userName: 'tester' });
      }
      if (url.includes('/api/v1/documents/summary')) return json({ draft: 0, submitted: 0, approved: 2, rejected: 0, withIssues: 0 });
      if (url.includes('/api/v1/documents/template-versions')) return json({ items: [], withoutVersionCount: 0 });
      if (url.includes('/api/v1/documents')) {
        const items = [item(1, [202603]), item(2, null)];
        return json({ items, nextCursor: null, totalCount: items.length });
      }
      if (url.includes('/api/v1/projects')) return json({ items: [{ id: 1, code: 'ATR', status: 'Active' }], nextCursor: null, totalCount: 1 });

      return json(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/documents?year=2026']}>
        <QueryClientProvider client={client}>
          <DocumentsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentsPage: джерело змінено після затвердження (F5-R, D-570)', () => {
  it(
    'рядок зі sourceChangedPeriods несе позначку з місяцями, рядок без поля — ні',
    async () => {
      mockFetch();
      show();

      const mark = await waitFor(
        () => {
          const found = document.querySelector('[data-document-row="DOC-000001"] [data-source-changed]');
          expect(found).not.toBeNull();
          return found!;
        },
        { timeout: SlowEnvTimeout },
      );
      expect(mark.getAttribute('data-periods')).toBe('202603');
      expect(mark.textContent).toContain('documents.sourceChanged');
      expect(document.querySelector('[data-document-row="DOC-000002"] [data-source-changed]')).toBeNull();
    },
    SlowEnvTimeout,
  );
});
