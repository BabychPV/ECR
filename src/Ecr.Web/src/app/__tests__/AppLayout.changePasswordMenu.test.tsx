import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { AppLayout } from '@/app/AppLayout';
import { routes } from '@/app/routes';

/**
 * `R-14`: меню профілю не мало шляху до зміни пароля добровільно — лише
 * примусовий редирект на `mustChangePassword`. Пункт меню має бути видимий
 * і вести саме на `routes.changePassword.path` (`/change-password`).
 *
 * ⛔ Мутаційний доказ: приберіть новий `<Menu.Item>` із `UserMenu.tsx` — цей
 * тест падає на `findByRole('menuitem', { name: '⟦password.title⟧' })`, бо
 * пункту немає в дереві взагалі.
 */
const MeResponse = {
  userId: 1,
  userName: 'tester',
  language: 'en',
  permissions: [],
  isSimulation: false,
  mustChangePassword: false,
};

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function renderAppLayout(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: <AppLayout />,
        children: [
          { index: true, element: <div data-testid="page-content">Main page content</div> },
          {
            path: routes.changePassword.path,
            element: <div data-testid="change-password-content">Change password screen</div>,
          },
        ],
      },
    ],
    { initialEntries: ['/'] },
  );

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me/preferences')) return jsonResponse([]);
      if (/\/api\/v1\/me(\?|$)/.test(url)) return jsonResponse(MeResponse);
      if (url.includes('/ui-strings/')) {
        return jsonResponse({ languageCode: 'en', revision: 1, strings: {} });
      }
      if (url.includes('/languages')) return jsonResponse([]);

      return jsonResponse(null);
    }),
  );

  render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('AppLayout → UserMenu: зміна пароля доступна з меню профілю (R-14)', () => {
  it('пункт меню веде на /change-password', async () => {
    renderAppLayout();

    const trigger = await screen.findByRole('button', { name: 'tester' });
    const user = userEvent.setup();
    await user.click(trigger);

    const changePasswordItem = await screen.findByRole('menuitem', {
      name: '⟦password.title⟧',
    });

    // ⛔ Не той самий пункт, що вихід: перевіряємо, що це окремий елемент
    // і що напис узятий саме з ключа `password.title`, а не з нового
    // англійського літерала.
    expect(screen.getByRole('menuitem', { name: '⟦profile.logout⟧' })).toBeDefined();

    await user.click(changePasswordItem);

    await waitFor(() => {
      expect(screen.getByTestId('change-password-content')).toBeDefined();
    });

    expect(screen.queryByTestId('page-content')).toBeNull();
  });
});
