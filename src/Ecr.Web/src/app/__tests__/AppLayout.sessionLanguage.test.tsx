import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { AppLayout } from '@/app/AppLayout';
import { resetSignOutForTests } from '@/api/client';
import { language, markLoginChosenLanguage, setLanguage } from '@/shared/i18n';

/**
 * A3 (приймальна №3), два дефекти оболонки після входу:
 *
 * 1. Мова з екрана входу не переносилась у сесію: форма російською, а після входу `html lang=en` —
 *    оболонка вантажила приватний каталог мовою профілю (`me.language` = en) і `loadCatalog` ставив
 *    `current = en`. Тепер вибір на логіні (`markLoginChosenLanguage`) — мова сесії й іде на сервер.
 * 2. `428 GET /api/v1/languages` при відкритті меню профілю на екрані примусової зміни пароля.
 *
 * ⛔ Мутаційні докази: прибрати `loginChosenLanguage()` із `sessionLanguageOf` (AppLayout) — червоніють
 * перші два випадки; прибрати `sync.changed('language', …)` з `usePreferenceSync` — червоніє PUT-випадок;
 * прибрати `enabled` із `useLanguages` — червоніє випадок про меню.
 */
function stubFetch(
  options: { mustChangePassword?: boolean; profileLanguage?: string; serverPreferences?: unknown[] } = {},
): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const lang = /\/ui-strings\/([a-z]+)/.exec(url)?.[1];
    const body = url.includes('/api/v1/me/preferences')
      ? init?.method === 'PUT'
        ? { key: 'language', value: 'ru' }
        : (options.serverPreferences ?? [])
      : /\/api\/v1\/me(\?|$)/.test(url)
        ? {
            userId: 1,
            userName: 'tester',
            language: options.profileLanguage ?? 'en',
            permissions: [],
            isSimulation: false,
            mustChangePassword: options.mustChangePassword ?? false,
          }
        : lang !== undefined
          ? { languageCode: lang, revision: 1, strings: {} }
          : url.includes('/languages')
            ? [
                { code: 'en', nameNative: 'English', isDefault: true, hasTranslations: true },
                { code: 'ru', nameNative: 'Русский', isDefault: false, hasTranslations: true },
              ]
            : url.includes('/api/v1/jobs')
              ? []
              : null;
    return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

function requested(fetchMock: ReturnType<typeof vi.fn>): string[] {
  return fetchMock.mock.calls.map((call) => String(call[0]));
}

function renderApp(path = '/'): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: <AppLayout />,
        children: [
          { index: true, element: <div>Головна</div>, handle: { labelKey: 'home.label' } },
          { path: 'change-password', element: <div>Зміна пароля</div>, handle: { labelKey: 'password.title' } },
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
}

afterEach(() => {
  resetSignOutForTests();
  vi.unstubAllGlobals();
  markLoginChosenLanguage(null);
  setLanguage('en');
  try {
    globalThis.localStorage?.clear();
  } catch {
    // сховища може не бути — тест його не потребує
  }
});

describe('AppLayout: мова сесії після входу (A3)', () => {
  it('мова з екрана входу (ru) перемагає профіль (en): каталог ru, мова сесії ru', async () => {
    const fetchMock = stubFetch({ profileLanguage: 'en' });
    setLanguage('ru');
    markLoginChosenLanguage('ru');

    renderApp();
    await screen.findByText('Головна');

    expect(requested(fetchMock).some((url) => url.includes('/ui-strings/ru') && url.includes('scope=private'))).toBe(true);
    expect(requested(fetchMock).some((url) => url.includes('/ui-strings/en') && url.includes('scope=private'))).toBe(false);
    expect(language()).toBe('ru');
  });

  it('вибір на логіні пишеться на сервер: PUT /me/preferences/language зі значенням ru', async () => {
    const fetchMock = stubFetch({ profileLanguage: 'en' });
    setLanguage('ru');
    markLoginChosenLanguage('ru');

    renderApp();

    await waitFor(() =>
      expect(
        fetchMock.mock.calls.some(
          (call) =>
            String(call[0]).endsWith('/api/v1/me/preferences/language') &&
            (call[1] as RequestInit | undefined)?.method === 'PUT' &&
            String((call[1] as RequestInit).body).includes('ru'),
        ),
      ).toBe(true),
    );
    expect(language()).toBe('ru');
  });

  it('збережене на сервері en не перекриває вибір на логіні (ключ позначено «зміненим»)', async () => {
    const fetchMock = stubFetch({
      profileLanguage: 'en',
      serverPreferences: [{ key: 'language', value: 'en' }],
    });
    setLanguage('ru');
    markLoginChosenLanguage('ru');

    renderApp();
    await screen.findByText('Головна');
    // Відповідь GET /me/preferences приїхала й узгоджена — лише після цього мова ще раз перевіряється.
    await waitFor(() =>
      expect(requested(fetchMock).some((url) => url.endsWith('/api/v1/me/preferences'))).toBe(true),
    );
    await new Promise((resolve) => setTimeout(resolve, 100));

    expect(language()).toBe('ru');
  });

  it('контроль: без вибору на логіні — мова профілю (en), запису на сервер немає', async () => {
    const fetchMock = stubFetch({ profileLanguage: 'en' });

    renderApp();
    await screen.findByText('Головна');
    await waitFor(() => expect(requested(fetchMock).some((url) => url.includes('/ui-strings/en'))).toBe(true));

    expect(language()).toBe('en');
    expect(
      fetchMock.mock.calls.some((call) => (call[1] as RequestInit | undefined)?.method === 'PUT'),
    ).toBe(false);
  });
});

describe('AppLayout: меню профілю під примусовою зміною пароля (A3)', () => {
  it('відкриття меню не дає GET /api/v1/languages', async () => {
    const fetchMock = stubFetch({ mustChangePassword: true });
    const user = userEvent.setup();

    renderApp('/change-password');
    await screen.findByText('Зміна пароля');
    await waitFor(() => expect(requested(fetchMock).some((url) => url.includes('/ui-strings/'))).toBe(true));

    await user.click(screen.getByText('tester'));
    await screen.findByRole('menuitem', { name: '⟦profile.logout⟧' });
    await new Promise((resolve) => setTimeout(resolve, 100));

    expect(requested(fetchMock).filter((url) => url.includes('/languages'))).toEqual([]);
  });
});
