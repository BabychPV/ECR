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
import { testTheme } from '@/test/render';

/**
 * Текстові/числові фільтри: один набір — один запит.
 *
 * ⛔ Живий стенд (2026-09-24): набір `R12345` у «Row key» на `/admin/audit`
 * давав шість `GET /api/v1/audit/cells?…&rowKey=R`, `R1`, … `R12345` — по
 * партиціонованому журналу. Предмет перевірки — ЛІЧИЛЬНИК запитів із
 * параметром фільтра, а не вигляд поля.
 *
 * ⛔ Мутаційний доказ: у `useDebouncedFilter` повернути `value` замість
 * `debounced` — кожен тест нижче бачить по запиту на КОЖНУ клавішу
 * (`['R', 'R1', …]`) і червоніє.
 */

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

const me = {
  denies: [],
  grants: {},
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: [],
  simulatedForUserId: null,
  userId: 1,
  userName: 'tester',
};

/** Значення `param` у кожному запиті до `path` (порожній рядок — параметра немає). */
function count(path: string, param: string): string[] {
  const seen: string[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), 'http://x');

      if (url.pathname === path) seen.push(url.searchParams.get(param) ?? '');
      if (url.pathname === '/api/v1/me') return json(me);

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

/** Чекає, доки мине пауза з запасом, — щоб «лише один запит» не був випадковістю таймінгу. */
const settle = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, FilterDebounceMs * 2));

/** Набирає `text` у поле й перевіряє: поле оновилось одразу, у мережу — рівно один запит. */
async function typeAndCount(label: string, text: string, seen: string[]): Promise<void> {
  const user = userEvent.setup();
  const input = await screen.findByRole('textbox', { name: label }, { timeout: SlowEnvTimeout });

  // Відкриття сторінки — один запит без фільтра.
  await waitFor(() => expect(seen).toEqual(['']));

  await user.type(input, text);
  // Значення в полі — одразу, без паузи.
  expect((input as HTMLInputElement).value).toBe(text);

  await waitFor(() => expect(seen).toEqual(['', text]));
  await settle();
  expect(seen).toEqual(['', text]);
}

describe('/admin/audit — журнал змін комірок', () => {
  it(
    '«Row key»: набір R12345 — один запит, а не шість',
    async () => {
      const seen = count('/api/v1/audit/cells', 'rowKey');
      // ⚠ З документом: без нього ключ рядка в запит не йде зовсім
      // (`AuditPage.cellNeedsDocument.test.tsx`).
      show(<AuditPage />, '/admin/audit?documentId=7');

      await typeAndCount('⟦audit.rowKey⟧', 'R12345', seen);
    },
    SlowEnvTimeout,
  );

  it(
    '«By user» (число): набір 12345 — один запит',
    async () => {
      const seen = count('/api/v1/audit/cells', 'author');
      show(<AuditPage />, '/admin/audit');

      await typeAndCount('⟦audit.author⟧', '12345', seen);
    },
    SlowEnvTimeout,
  );

  it(
    '«Document» (число): набір 12345 — один запит',
    async () => {
      const seen = count('/api/v1/audit/cells', 'documentId');
      show(<AuditPage />, '/admin/audit');

      await typeAndCount('⟦audit.document⟧', '12345', seen);
    },
    SlowEnvTimeout,
  );

  it(
    '«Column» (число): набір 12345 — один запит',
    async () => {
      const seen = count('/api/v1/audit/cells', 'columnDefId');
      show(<AuditPage />, '/admin/audit?documentId=7');

      await typeAndCount('⟦audit.columnDefId⟧', '12345', seen);
    },
    SlowEnvTimeout,
  );
});

describe('/admin/audit?view=structure — журнал структурних змін', () => {
  it(
    '«Entity type»: набір Template — один запит',
    async () => {
      const seen = count('/api/v1/audit/structure', 'entityType');
      show(<AuditPage />, '/admin/audit?view=structure');

      await typeAndCount('⟦audit.entityType⟧', 'Template', seen);
    },
    SlowEnvTimeout,
  );

  it(
    '«By user» (число): набір 12345 — один запит',
    async () => {
      const seen = count('/api/v1/audit/structure', 'changedByUserId');
      show(<AuditPage />, '/admin/audit?view=structure');

      await typeAndCount('⟦audit.author⟧', '12345', seen);
    },
    SlowEnvTimeout,
  );
});

describe('/admin/consistency — перелік розбіжностей', () => {
  it(
    '«Rule»: набір CNS-01 — один запит',
    async () => {
      const seen = count('/api/v1/consistency/issues', 'ruleCode');
      show(<ConsistencyIssuesPage />, '/admin/consistency');

      await typeAndCount('⟦consistency.rule⟧', 'CNS-01', seen);
    },
    SlowEnvTimeout,
  );
});
