import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * `BE-09`: смуга лічильників над переліком і два нові поля рядка.
 *
 * ⛔ Головне тут — різниця між «не перевіряли» (`null` → «—») і «перевірили,
 * чисто» (`0`): зелений нуль під неперевіреним документом — неправда `A7-28`.
 */
const base = { createdAt: '2026-01-01T00:00:00Z', projectId: 1, sheetCount: 1, sheetStates: {} };

const documents = [
  { ...base, id: 1, businessKey: 'NEVER-VALIDATED', errorCount: null, warningCount: null, modifiedAt: null, modifiedByDisplayName: null },
  { ...base, id: 2, businessKey: 'CLEAN', errorCount: 0, warningCount: 0, modifiedAt: '2026-01-20T09:00:00Z', modifiedByDisplayName: 'Olena Editor' },
];

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

const requested: string[] = [];

function mockFetch(rejected = 0): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      requested.push(url);

      if (url.includes('/api/v1/documents/summary')) {
        return json({ draft: 7, submitted: 3, approved: 12, rejected, withIssues: 4 });
      }

      if (url.includes('/api/v1/documents')) {
        return json({ items: documents, nextCursor: null, totalCount: 2 });
      }

      if (url.includes('/api/v1/projects')) {
        return json({ items: [], nextCursor: null, totalCount: 0 });
      }

      return json(null);
    }),
  );
}

function show(path: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[path]}>
        <QueryClientProvider client={client}>
          <DocumentsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  requested.length = 0;
});

const SlowEnvTimeout = 400_000;

describe('DocumentsPage: зведення переліку (BE-09)', () => {
  it(
    'смуга показує числа сервера за період з адреси; кнопки — лише лічильники стану',
    async () => {
      mockFetch();
      show('/?periodKey=202601');

      const strip = await screen.findByRole('group', { name: /documents\.summaryLabel|Documents by state/ }, { timeout: SlowEnvTimeout });
      const counter = (id: string): string =>
        strip.querySelector(`[data-summary-counter="${id}"]`)?.textContent ?? '';

      expect(counter('draft')).toContain('7');
      expect(counter('submitted')).toContain('3');
      expect(counter('approved')).toContain('12');
      expect(counter('withIssues')).toContain('4');
      // `BE-09b`: лічильники стану фільтрують (див. `DocumentListSummaryStrip.filter.test.tsx`);
      // «з проблемами» — не стан, фільтра за ним немає.
      expect(within(strip).queryAllByRole('button')).toHaveLength(3);
      expect(strip.querySelector('[data-summary-counter="withIssues"]')?.tagName).toBe('DIV');
      expect(requested.some((url) => url.includes('/documents/summary?periodKey=202601'))).toBe(true);
    },
    SlowEnvTimeout,
  );

  it(
    'відхилені стоять одразу після поданих, у тоні відмови — лише коли вони є',
    async () => {
      mockFetch(2);
      show('/?periodKey=202601');

      const strip = await screen.findByRole('group', { name: /documents\.summaryLabel|Documents by state/ }, { timeout: SlowEnvTimeout });
      const order = [...strip.querySelectorAll('[data-summary-counter]')].map((node) => node.getAttribute('data-summary-counter'));
      const rejected = strip.querySelector('[data-summary-counter="rejected"]');

      expect(order).toEqual(['draft', 'submitted', 'rejected', 'approved', 'withIssues']);
      expect(rejected?.textContent).toContain('2');
      expect(rejected?.getAttribute('data-summary-tone')).toBe('danger');
    },
    SlowEnvTimeout,
  );

  it(
    'коли відхилених немає, лічильника немає зовсім — не «0»',
    async () => {
      mockFetch(0);
      show('/?periodKey=202601');

      const strip = await screen.findByRole('group', { name: /documents\.summaryLabel|Documents by state/ }, { timeout: SlowEnvTimeout });

      expect(strip.querySelectorAll('[data-summary-counter]')).toHaveLength(4);
      expect(strip.querySelector('[data-summary-counter="rejected"]')).toBeNull();
      expect(strip.querySelector('[data-summary-tone]')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'без періоду зведення не запитується і смуги немає',
    async () => {
      mockFetch();
      show('/');

      await screen.findByRole('table', {}, { timeout: SlowEnvTimeout });

      expect(requested.some((url) => url.includes('/documents/summary'))).toBe(false);
      expect(document.querySelector('[data-summary-counter]')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'неперевірений документ показує «—», а перевірений і чистий — «0»',
    async () => {
      mockFetch();
      show('/?periodKey=202601');

      const table = await screen.findByRole('table', {}, { timeout: SlowEnvTimeout });
      const cells = table.querySelectorAll('[data-document-errors]');

      expect(cells[0]?.getAttribute('data-document-errors')).toBe('none');
      expect(cells[0]?.textContent).toBe('—');
      expect(cells[1]?.textContent).toBe('0');
      await within(table).findByText('Olena Editor');
    },
    SlowEnvTimeout,
  );
});
