import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnapshotsPage } from '@/pages/admin/SnapshotsPage';
import { testTheme } from '@/test/render';
import { formatDateTime } from '@/shared/format';

/**
 * `UI-07`: момент побудови зрізу читабельний на екрані й ТОЧНИЙ у розмітці.
 *
 * ⛔ Обидві половини обов'язкові — див. `JobsPage.timestamp.test.tsx`. Тут
 * друга важить навіть більше: зріз незмінний, і момент його побудови — частина
 * доказу, з якою звіряють роздрукований звіт.
 *
 * ⚠ Очікуваний текст — з `formatDateTime(...)`, а не дослівний рядок: інакше це
 * був би тест версії ICU у Node, а не продукту.
 */
const Built = '2026-01-15T10:00:00Z';

const snapshot = {
  id: 1,
  builtAt: Built,
  contentHash: 'abc123',
  isCurrent: true,
  periodKey: 202601,
  projectId: 42,
  reportVersionId: 1,
  rowCount: 10,
  status: 'Approved',
};

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/reports/snapshots')) return json([snapshot]);
      if (url.includes('/api/v1/reports')) return json([]);

      if (url.includes('/api/v1/projects')) {
        return json({
          items: [{ id: 42, code: 'KASH_2026', status: 'Active' }],
          nextCursor: null,
          totalCount: 1,
        });
      }

      if (url.includes('/api/v1/me')) {
        return json({ denies: [], grants: {}, permissions: [], userId: 1, userName: 'tester' });
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

/**
 * Чекає момент побудови в розмітці й віддає сам елемент.
 *
 * ⚠ Запит звужений до таблиці: сторінка тримає ще й модальні вікна та
 * селектори, і `document.querySelector('time')` без цієї межі міг би дійти до
 * чужого елемента, щойно на сторінці з'явиться другий момент.
 */
async function moment(): Promise<HTMLTimeElement> {
  const table = await screen.findByRole('table', {}, { timeout: 20_000 });

  return await waitFor(() => {
    const node = table.querySelector('time');

    expect(node, 'момент побудови не намальовано елементом <time>').not.toBeNull();

    return node as HTMLTimeElement;
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SnapshotsPage: момент побудови — читабельний на екрані, точний у розмітці', () => {
  it(
    'показує момент мовою набору, а не сирий рядок сервера',
    async () => {
      mockFetch();
      show();

      const node = await moment();

      expect(node.textContent).not.toBe(Built);
      expect(node.textContent).not.toMatch(/T\d{2}:\d{2}/);
      expect(screen.queryByText(Built), 'сирий рядок лишився видимим текстом').toBeNull();

      expect(node.textContent).toBe(formatDateTime(Built));
    },
    30_000,
  );

  it(
    'точне значення лишилося в розмітці — зріз незмінний, момент звіряють',
    async () => {
      mockFetch();
      show();

      const node = await moment();

      expect(node.getAttribute('datetime')).toBe(Built);
      expect(node.getAttribute('title')).toBe(Built);
    },
    30_000,
  );
});
