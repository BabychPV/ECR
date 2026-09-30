import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuditPage } from '@/pages/admin/AuditPage';
import { FilterDebounceMs } from '@/shared/ui/useDebouncedFilter';
import { testTheme } from '@/test/render';

/**
 * «Row key»/«Column» без «Document» у запит не йдуть.
 *
 * ⛔ Живий стенд (2026-09-24): після debounce набір `R12345` без документа
 * лишав рівно один запит — і той гарантовано `422 ECR-REQ-0422`: ключ рядка
 * унікальний лише в межах документа (`AuditController.Cells`). Тепер такого
 * запиту немає, а причину видно: пояснення `audit.cellHint` під рядом
 * фільтрів виділяється.
 *
 * ⛔ Мутаційний доказ: у `AuditPage` поставити `cellNeedsDocument = false` —
 * перший тест бачить запит із `rowKey=R12345` і червоніє; прибрати `active`
 * у `FilterHints` — червоніє перевірка видимої причини.
 */

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

/** Кожен запит журналу комірок як `URLSearchParams`. */
function count(): URLSearchParams[] {
  const seen: URLSearchParams[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), 'http://x');

      if (url.pathname === '/api/v1/audit/cells') seen.push(url.searchParams);
      if (url.pathname === '/api/v1/me') return json({ permissions: [], denies: [], grants: {} });

      return json({ items: [], nextCursor: null, totalCount: 0 });
    }),
  );

  return seen;
}

function show(path: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[path]}>
        <QueryClientProvider client={client}>
          <AuditPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const settle = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, FilterDebounceMs * 2));

const activeHint = (): Element | null => document.querySelector('[data-filter-hint-active="true"]');

describe('AuditPage: адреса комірки без документа', () => {
  it(
    'набір у «Row key» без «Document» — 0 запитів, і видно чому',
    async () => {
      const seen = count();
      const user = userEvent.setup();
      show('/admin/audit');

      const rowKey = await screen.findByRole('textbox', { name: '⟦audit.rowKey⟧' }, { timeout: SlowEnvTimeout });
      await waitFor(() => expect(seen).toHaveLength(1));
      expect(activeHint()).toBeNull();

      await user.type(rowKey, 'R12345');
      await settle();

      expect((rowKey as HTMLInputElement).value).toBe('R12345');
      expect(seen).toHaveLength(1);
      expect(seen.some((query) => query.has('rowKey'))).toBe(false);
      expect(activeHint()?.textContent).toBe('⟦audit.cellHint⟧');
    },
    SlowEnvTimeout,
  );

  it(
    '«Column» без «Document» — теж 0 запитів',
    async () => {
      const seen = count();
      const user = userEvent.setup();
      show('/admin/audit');

      const column = await screen.findByRole('textbox', { name: '⟦audit.columnDefId⟧' }, { timeout: SlowEnvTimeout });
      await waitFor(() => expect(seen).toHaveLength(1));

      await user.type(column, '11');
      await settle();

      expect(seen).toHaveLength(1);
      expect(activeHint()?.textContent).toBe('⟦audit.cellHint⟧');
    },
    SlowEnvTimeout,
  );

  it(
    'далі «Document» — рівно 1 запит, і вже з набраним ключем рядка; причина зникає',
    async () => {
      const seen = count();
      const user = userEvent.setup();
      show('/admin/audit?rowKey=R12345');

      const documentField = await screen.findByRole('textbox', { name: '⟦audit.document⟧' }, { timeout: SlowEnvTimeout });
      await waitFor(() => expect(seen).toHaveLength(1));
      expect(seen[0]?.has('rowKey')).toBe(false);

      await user.type(documentField, '7');
      await waitFor(() => expect(seen).toHaveLength(2));
      await settle();

      expect(seen).toHaveLength(2);
      expect(seen[1]?.get('documentId')).toBe('7');
      expect(seen[1]?.get('rowKey')).toBe('R12345');
      expect(activeHint()).toBeNull();
    },
    SlowEnvTimeout,
  );
});
