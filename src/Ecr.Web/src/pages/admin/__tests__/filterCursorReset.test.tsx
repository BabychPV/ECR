import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { AuditPage } from '@/pages/admin/AuditPage';
import { ConsistencyIssuesPage } from '@/pages/admin/ConsistencyIssuesPage';
import { FilterDebounceMs } from '@/shared/ui/useDebouncedFilter';
import { t } from '@/shared/i18n';
import { testTheme } from '@/test/render';

/**
 * Зміна фільтра ПІСЛЯ гортання «More» — рівно один запит.
 *
 * ⛔ До виправлення курсор скидався від СИРОГО значення поля (`setCursor(null)`
 * в `onChange`), а фільтр застосовувався від відкладеного. Перша ж клавіша
 * давала запит «старий фільтр, перша сторінка», і лише після паузи — запит
 * із новим фільтром: два запити на один набір.
 *
 * ⛔ Мутаційний доказ: у `useFilterCursor` повернути `state.cursor` без
 * перевірки `forKey` — курсор переживає зміну фільтра, і запит із новим
 * фільтром іде з курсором старої видачі (тести червоніють на `cursor`);
 * повернути в `onChange` `setCursor(null)` — з'являється зайвий запит.
 */

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

const structureRow = {
  changedAt: '2026-01-05T10:00:00Z',
  changedByUserId: 41,
  entityType: 'cfg.Template',
  entityId: 3,
  operation: 'Update',
  changeReason: null,
};

const issueRow = {
  id: 1,
  ruleCode: 'CNS-01',
  entityType: 'cfg.Template',
  entityId: 3,
  message: 'broken',
  detectedAt: '2026-01-05T10:00:00Z',
  resolvedAt: null,
};

/**
 * Кожен запит до `path`; перша сторінка (без курсора) віддає `nextCursor`,
 * тож кнопка «More» з'являється.
 */
function serve(path: string, row: unknown): URLSearchParams[] {
  const seen: URLSearchParams[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), 'http://x');

      if (url.pathname === '/api/v1/me') return json({ permissions: [], denies: [], grants: {} });
      if (url.pathname === path) {
        seen.push(url.searchParams);
        const cursor = url.searchParams.get('cursor');

        return json({ items: [row], nextCursor: cursor === null ? 'page-2' : null, totalCount: null });
      }

      return json({ items: [], nextCursor: null, totalCount: 0 });
    }),
  );

  return seen;
}

function show(page: JSX.Element, path: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[path]}>
        <QueryClientProvider client={client}>{page}</QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const settle = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, FilterDebounceMs * 2));

/** Гортає на другу сторінку, набирає `text` у поле й повертає запити ПІСЛЯ гортання. */
async function pageThenType(seen: URLSearchParams[], label: string, text: string): Promise<URLSearchParams[]> {
  const user = userEvent.setup();
  const more = await screen.findByRole('button', { name: t('documents.more') }, { timeout: SlowEnvTimeout });

  await user.click(more);
  await waitFor(() => expect(seen).toHaveLength(2));
  expect(seen[1]?.get('cursor')).toBe('page-2');

  await user.type(screen.getByRole('textbox', { name: label }), text);
  await waitFor(() => expect(seen.length).toBeGreaterThan(2));
  await settle();

  return seen.slice(2);
}

describe('скидання курсора разом із відкладеним фільтром', () => {
  // ✎ `R-18`: «By user» став вибором за іменем (`Select`) — набору, який треба
  // відкладати, у нього більше немає; скидання курсора стережуть поля нижче.

  it(
    '/admin/audit?view=structure: «Entity type» після «More» — один запит',
    async () => {
      const seen = serve('/api/v1/audit/structure', structureRow);
      show(<AuditPage />, '/admin/audit?view=structure');

      const after = await pageThenType(seen, '⟦audit.entityType⟧', 'cfg');

      expect(after.map((query) => [query.get('entityType'), query.get('cursor')])).toEqual([['cfg', null]]);
    },
    SlowEnvTimeout,
  );

  it(
    '/admin/consistency: «Rule» після «More» — один запит',
    async () => {
      const seen = serve('/api/v1/consistency/issues', issueRow);
      show(<ConsistencyIssuesPage />, '/admin/consistency');

      const after = await pageThenType(seen, '⟦consistency.rule⟧', 'CNS');

      expect(after.map((query) => [query.get('ruleCode'), query.get('cursor')])).toEqual([['CNS', null]]);
    },
    SlowEnvTimeout,
  );
});
