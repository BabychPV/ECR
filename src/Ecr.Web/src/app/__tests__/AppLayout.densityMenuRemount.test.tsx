import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { AppLayout } from '@/app/AppLayout';

/**
 * Регрес «щільність приїжджає з сервера, поки меню профілю відкрите»
 * (`refactor/usermenu-density-registry`).
 *
 * ⛔ ДО фіксу `AppLayout` рендерив `<UserMenu key={preferencesGeneration}
 * …>`: `usePreferenceSync` (`BE-20`) повертала зростаюче число щоразу, коли
 * значення щільності з `GET /api/v1/me/preferences` розходилося з
 * `localStorage`-кешем першого рендера. Нове число в `key` форсувало
 * React ПОВНІСТЮ розмонтувати й перемонтувати `UserMenu` — разом із
 * власним, уже ВІДКРИТИМ `Menu` усередині. Людина, що саме дивилася в
 * меню профілю (типовий момент: щойно увійшла, відкрила «Профіль», а
 * відповідь `/preferences` ще в польоті), бачила меню, що закривається
 * само собою.
 *
 * Фікс: `UserMenu` сам підписаний на щільність (`useDensity()`,
 * `shared/theme/preferences.ts`, `useSyncExternalStore`) і оновлюється
 * природним рендером; `AppLayout` більше не форсує ремонт.
 *
 * Мутаційний доказ: поверни `key={preferencesGeneration}` на `<UserMenu>`
 * в `AppLayout.tsx` АБО поверни в `UserMenu.tsx` одноразовий
 * `useState<Density>(density)` замість `useDensity()` — цей тест впаде:
 * або радіо-кнопки зникають із дерева (меню закрилося), або значення
 * `compact` лишається позначеним назавжди (перемикач не побачив нового
 * значення).
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

function renderAppLayout(preferences: Promise<unknown>): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: <AppLayout />,
        children: [
          { index: true, element: <div data-testid="page-content">Main page content</div> },
        ],
      },
    ],
    { initialEntries: ['/'] },
  );

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      // ⛔ Точний збіг: `/api/v1/me/preferences` теж містить підрядок
      // `/api/v1/me` — перевіряється РАНІШЕ, інакше обидва ендпоінти
      // відповідали б профілем (`a11yFixtures.tsx` уже наступала на це).
      if (url.includes('/api/v1/me/preferences')) return preferences.then(jsonResponse);
      if (/\/api\/v1\/me(\?|$)/.test(url)) return jsonResponse(MeResponse);
      if (url.includes('/ui-strings/')) {
        return jsonResponse({ languageCode: 'en', revision: 1, strings: {} });
      }

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

describe('AppLayout → UserMenu: синхронізація щільності з сервера не закриває відкрите меню', () => {
  it('меню лишається відкритим і перемикач показує нове значення без ремонту', async () => {
    let resolvePreferences: (value: unknown) => void = () => {};
    const preferences = new Promise((resolve) => {
      resolvePreferences = resolve;
    });

    renderAppLayout(preferences);

    const trigger = await screen.findByRole('button', { name: 'tester' });
    const user = userEvent.setup();
    await user.click(trigger);

    // Меню відкрите: `localStorage` порожній ⇒ дефолт `compact`
    // (`shared/theme/preferences.ts:density()`).
    const compactBefore = (await screen.findByRole('radio', {
      name: '⟦profile.densityCompact⟧',
    })) as HTMLInputElement;
    const comfortableBefore = screen.getByRole('radio', {
      name: '⟦profile.densityComfortable⟧',
    }) as HTMLInputElement;
    expect(compactBefore.checked).toBe(true);
    expect(comfortableBefore.checked).toBe(false);

    // Відповідь сервера приходить ПІЗНІШЕ, ніж людина відкрила меню — саме
    // той порядок подій, у якому жив дефект.
    resolvePreferences([{ key: 'density', value: 'comfortable' }]);

    // ⛔ Якщо ремонт через `key` повернувся, `Menu` закривається і
    // `SegmentedControl` зникає з дерева: `getByRole` усередині `waitFor`
    // кидає «unable to find role» і тест падає тут, а не на значенні.
    await waitFor(() => {
      const comfortable = screen.getByRole('radio', {
        name: '⟦profile.densityComfortable⟧',
      }) as HTMLInputElement;
      expect(comfortable.checked).toBe(true);
    });

    const compactAfter = screen.getByRole('radio', {
      name: '⟦profile.densityCompact⟧',
    }) as HTMLInputElement;
    expect(compactAfter.checked).toBe(false);

    // Меню й далі відкрите — вихід (пункт нижче в тому самому `Menu.Dropdown`)
    // усе ще в дереві.
    expect(screen.getByText('⟦profile.logout⟧')).toBeDefined();
  });
});
