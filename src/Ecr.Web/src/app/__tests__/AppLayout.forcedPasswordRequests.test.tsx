import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { AppLayout } from '@/app/AppLayout';
import { resetSignOutForTests } from '@/api/client';

/**
 * A2-06 (приймальна №2): екран примусової зміни пароля слав `GET /api/v1/jobs` (8× 428) —
 * «My tasks» у шапці опитує власні задачі кожні 3–30 с, а під разовим паролем сервер на все,
 * крім зміни пароля, `/me`, каталогу й виходу, відповідає 428. А після «Вийти» живий застосунок
 * встигав піти ще запитами вже без cookie (401).
 *
 * ⛔ Мутаційний доказ: поверніть `<MyTasksLauncher />` поза умовою `!me.mustChangePassword` в
 * `AppLayout.tsx` — другий тест падає (є запит `/api/v1/jobs`); приберіть `beginSignOut()` із
 * `signOut` (`UserMenu.tsx`) — третій тест падає (запит після виходу пішов у мережу).
 */
function stubFetch(mustChangePassword: boolean): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input);
    const body = url.includes('/api/v1/me/preferences')
      ? []
      : /\/api\/v1\/me(\?|$)/.test(url)
        ? {
            userId: 1,
            userName: 'tester',
            language: 'en',
            permissions: [],
            isSimulation: false,
            mustChangePassword,
          }
        : url.includes('/ui-strings/')
          ? { languageCode: 'en', revision: 1, strings: {} }
          : url.includes('/api/v1/jobs') || url.includes('/languages')
            ? []
            : null;
    return new Response(JSON.stringify(body), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    });
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

function requested(fetchMock: ReturnType<typeof vi.fn>): string[] {
  return fetchMock.mock.calls.map((call) => String(call[0]));
}

function renderApp(path: string): QueryClient {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: <AppLayout />,
        children: [
          { index: true, element: <div>Головна</div>, handle: { labelKey: 'home.label' } },
          {
            path: 'change-password',
            element: <div>Зміна пароля</div>,
            handle: { labelKey: 'password.title' },
          },
        ],
      },
    ],
    { initialEntries: [path] },
  );

  render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return client;
}

afterEach(() => {
  resetSignOutForTests();
  vi.unstubAllGlobals();
});

describe('AppLayout: зайві запити під примусовою зміною пароля і після виходу (A2-06)', () => {
  it('контроль: без прапорця шапка опитує власні задачі', async () => {
    const fetchMock = stubFetch(false);
    renderApp('/');

    await screen.findByText('Головна');
    await waitFor(() => expect(requested(fetchMock).some((url) => url.includes('/api/v1/jobs'))).toBe(true));
    expect(screen.getByRole('button', { name: '⟦jobs.myTasks⟧' })).toBeTruthy();
  });

  it('з mustChangePassword — жодного запиту /api/v1/jobs, кнопок «My tasks» і пошуку немає', async () => {
    const fetchMock = stubFetch(true);
    const client = renderApp('/change-password');

    await screen.findByText('Зміна пароля');
    // Профіль і приватний каталог уже приїхали — каркас змонтований повністю.
    await waitFor(() => expect(requested(fetchMock).some((url) => url.includes('/ui-strings/'))).toBe(true));

    expect(requested(fetchMock).filter((url) => url.includes('/api/v1/jobs'))).toEqual([]);
    expect(client.getQueryCache().find({ queryKey: ['jobs', true] })).toBeUndefined();
    expect(screen.queryByRole('button', { name: '⟦jobs.myTasks⟧' })).toBeNull();
    // Меню профілю з виходом лишається.
    expect(screen.getByText('tester')).toBeTruthy();
  });

  it('після «Вийти» в мережу йде лише сам вихід, перезапит профілю — ні', async () => {
    const fetchMock = stubFetch(true);
    const client = renderApp('/change-password');
    const user = userEvent.setup();

    await screen.findByText('Зміна пароля');
    await user.click(screen.getByText('tester'));
    await user.click(await screen.findByRole('menuitem', { name: '⟦profile.logout⟧' }));

    await waitFor(() => expect(requested(fetchMock)).toContain('/api/v1/logout'));
    const before = fetchMock.mock.calls.length;

    // Те, що робить фоновий перезапит між виходом і перезавантаженням сторінки.
    await client.refetchQueries({ queryKey: ['me'] });

    expect(fetchMock.mock.calls.length).toBe(before);
  });
});
