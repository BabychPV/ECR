import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { AppLayout } from '@/app/AppLayout';
import { childPath, routes } from '@/app/routes';
import { loadCatalog, setLanguage, t } from '@/shared/i18n';

/**
 * T3-02: зміна мови в меню перекладала оболонку, але вже відкрита сторінка лишалась англійською до F5 —
 * елемент `<Outlet/>` для React не змінюється, а сторінка сама на каталог не підписана.
 *
 * ⚠ Сторінку замінює шпигун, який читає `t()` БЕЗ `useCatalog()` — як більшість реальних сторінок.
 */
const Catalogs: Record<string, Record<string, string>> = {
  en: { 'probe.text': 'Hello' },
  ru: { 'probe.text': 'Привет' },
};

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function ProbePage(): JSX.Element {
  return <div data-testid="probe">{t('probe.text')}</div>;
}

describe('AppLayout: зміна мови перемальовує відкриту сторінку', () => {
  beforeEach(() => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);

        if (url.includes('/api/v1/me')) {
          return jsonResponse({
            userId: 1, userName: 'tester', language: 'en', permissions: [], isSimulation: false, mustChangePassword: false,
          });
        }

        const match = /\/ui-strings\/([a-z]+)/.exec(url);
        if (match !== null) {
          const lang = match[1] ?? 'en';
          return jsonResponse({ languageCode: lang, revision: 1, strings: Catalogs[lang] ?? {} });
        }

        return jsonResponse(null);
      }),
    );
    setLanguage('en');
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    setLanguage('en');
  });

  it('після зміни мови текст сторінки — новою мовою без перезавантаження', async () => {
    const router = createMemoryRouter(
      [
        {
          path: '/',
          element: <AppLayout />,
          children: [{ path: childPath(routes.documentDetail), element: <ProbePage />, handle: routes.documentDetail.handle }],
        },
      ],
      { initialEntries: ['/documents/12'] },
    );

    render(
      <MantineProvider theme={theme}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <RouterProvider router={router} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    expect((await screen.findByTestId('probe')).textContent).toBe('Hello');

    await act(async () => {
      setLanguage('ru');
      await loadCatalog('ru', 'private');
    });

    // ⛔ Мутація «прибрати `key={pageLanguage}` із `<Suspense>`» лишає «Hello» — тест червоний.
    expect((await screen.findByTestId('probe')).textContent).toBe('Привет');
  });
});
