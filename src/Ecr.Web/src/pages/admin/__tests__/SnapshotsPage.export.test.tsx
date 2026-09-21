import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnapshotsPage } from '@/pages/admin/SnapshotsPage';
import { testTheme } from '@/test/render';

/**
 * Вивантаження зрізу в книгу (`R7`, `GET …/snapshots/{id}/export.xlsx`).
 *
 * ⛔ Посилання, а не кнопка з `fetch`: вивантаження автентифікується тією
 * самою cookie, що й сторінка, тож браузер завантажує книгу сам.
 *
 * ⛔ Право `Report.Export` — окреме від перегляду: книга виходить за межі
 * системи, і той, хто може подивитися зріз на екрані, не обов'язково може
 * винести його назовні. Без права дії немає зовсім — показана й непрацездатна
 * вона обіцяла б те, що сервер відхилить.
 *
 * ⚠ Межа Excel (15 значущих цифр) названа ПОРУЧ із дією: той, хто звіряє до
 * останнього знаку, мусить дізнатися про це ДО вивантаження.
 */

const project = { id: 42, code: 'KASH_2026', status: 'Active' as const };

const snapshot = {
  id: 7,
  builtAt: '2026-01-15T10:00:00Z',
  contentHash: 'abc',
  isCurrent: true,
  periodKey: 202601,
  projectId: 42,
  reportVersionId: 1,
  rowCount: 10,
  status: 'Approved',
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function mockFetch(permissions: string[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions,
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (url.includes('/api/v1/reports/snapshots')) return json([snapshot]);
      if (url.includes('/api/v1/reports')) return json([]);

      if (url.includes('/api/v1/projects')) {
        return json({ items: [project], nextCursor: null, totalCount: 1 });
      }

      return json(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/snapshots']}>
        <QueryClientProvider client={client}>
          <SnapshotsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('SnapshotsPage: вивантаження зрізу в книгу (R7)', () => {
  it(
    'із правом Report.Export — посилання на саме цей зріз, із названою межею Excel',
    async () => {
      mockFetch(['Report.ViewRegulatory', 'Report.Export']);
      show();

      const link = await screen.findByRole(
        'link',
        { name: /snapshots\.export⟧/ },
        { timeout: SlowEnvTimeout },
      );

      /*
       * ⛔ Адреса перевіряється з ідентифікатором зрізу, а не «містить
       * export.xlsx»: посилання на ЧУЖИЙ зріз виглядало б так само.
       */
      expect(link.getAttribute('href')).toBe('/api/v1/reports/snapshots/7/export.xlsx');

      // ⚠ `download` — саме атрибут: без нього браузер відкрив би книгу як
      // навігацію, і сторінка зі зрізами зникла б з-під користувача.
      expect(link.hasAttribute('download')).toBe(true);

      // ⚠ Межа Excel має бути названа біля дії, а не в довідці — як опис
      // самого посилання (`aria-describedby`), а не `title`, якого не видно з
      // клавіатури (`SnapshotsPage.keyboardHints`).
      expect(link.hasAttribute('title')).toBe(false);
      expect(screen.queryAllByRole('link', { description: '⟦snapshots.exportHint⟧' })).toContain(link);
    },
    SlowEnvTimeout,
  );

  it(
    'без права Report.Export дії немає зовсім, а перегляд рядків лишається',
    async () => {
      mockFetch(['Report.ViewRegulatory']);
      show();

      /*
       * ⚠ Спершу дочекатися сусідньої дії того самого рядка — вона від права
       * вивантаження не залежить. Інакше твердження «посилання немає» було б
       * зеленим просто тому, що таблиця ще не приїхала.
       */
      const rows = await screen.findByText(/snapshots\.viewRows⟧/, {}, { timeout: SlowEnvTimeout });
      const cell = rows.closest('td');

      expect(cell).not.toBeNull();
      expect(within(cell as HTMLElement).queryByRole('link')).toBeNull();
    },
    SlowEnvTimeout,
  );
});
