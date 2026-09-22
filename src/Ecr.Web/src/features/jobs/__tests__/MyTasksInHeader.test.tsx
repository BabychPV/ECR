import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { AppLayout } from '@/app/AppLayout';

/**
 * «My tasks» у шапці — УСІМ РОЛЯМ (директива №15, бекенд, §BE-08).
 *
 * ⛔ Профіль нижче не має ЖОДНОГО права — навіть `System.ViewHealth`, під яким
 * закритий увесь екран `#/admin/jobs` (`routes.ts`). Саме такий користувач і є
 * предметом `UX-09`: він ставить експорт, закриває вкладку й досі не має як
 * дізнатися, чи файл готовий (`W-09`). Кнопка мусить бути йому видима.
 *
 * ⛔ Мутаційний доказ вимоги «усі ролі»: обгорніть `<MyTasksLauncher/>` в
 * `AppLayout.tsx` умовою `can(me, 'System.ViewHealth')` (або будь-яким іншим
 * правом) — і цей тест почервоніє, бо в профілі прав немає. Дзеркало навмисне:
 * перевірка права тут була б регресом, а не посиленням.
 *
 * ⚠ Тест лежить у `features/jobs/__tests__`, а не поруч із рештою тестів
 * `AppLayout`: він перевіряє вимогу до ЦІЄЇ функції, і жити має разом із нею.
 */
const MeWithoutPermissions = {
  userId: 7,
  userName: 'operator',
  language: 'en',
  permissions: [] as string[],
  isSimulation: false,
  mustChangePassword: false,
};

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

/** Скільки разів шапка сходила по ВЛАСНІ задачі. */
const jobRequests: string[] = [];

beforeEach(() => {
  jobRequests.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) return Promise.resolve(jsonResponse(MeWithoutPermissions));

      if (url.includes('/ui-strings/')) {
        return Promise.resolve(jsonResponse({ languageCode: 'en', revision: 1, strings: {} }));
      }

      if (url.includes('/api/v1/jobs')) {
        jobRequests.push(url);

        return Promise.resolve(jsonResponse([]));
      }

      return Promise.resolve(jsonResponse(null));
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function renderShell(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: <AppLayout />,
        children: [{ index: true, element: <div data-testid="page-content">page</div> }],
      },
    ],
    { initialEntries: ['/'] },
  );

  render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('шапка застосунку: «My tasks»', () => {
  it('кнопка видима користувачеві БЕЗ жодного права', async () => {
    renderShell();

    expect(await screen.findByRole('button', { name: '⟦jobs.myTasks⟧' })).toBeTruthy();
  });

  it('шапка питає лише ВЛАСНІ задачі (mine=true), і шухляда закрита (L2)', async () => {
    renderShell();

    await screen.findByRole('button', { name: '⟦jobs.myTasks⟧' });

    /*
     * ⛔ Без `mine=true` сервер відповів би `403` користувачеві без
     * `System.ViewHealth` — тобто шапка показувала б стан відмови на кожній
     * сторінці. Мутація «прибрати mine» червонить обидва твердження.
     */
    expect(jobRequests.length).toBeGreaterThan(0);
    expect(jobRequests.every((url) => url.includes('mine=true'))).toBe(true);

    expect(screen.queryByRole('dialog')).toBeNull();
  });
});
