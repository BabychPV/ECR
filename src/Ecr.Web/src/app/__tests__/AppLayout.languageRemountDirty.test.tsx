import { useEffect, type JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { AppLayout } from '@/app/AppLayout';
import { childPath, routes } from '@/app/routes';
import { loadCatalog, setLanguage, t } from '@/shared/i18n';
import { registerUnsavedSource } from '@/shared/ui/unsavedSources';

/**
 * T3-02, доробка: remount сторінки при зміні мови не має знищувати незбережений ввід.
 * Брудний стан → спершу `flushUnsaved`; збережено — сторінка перемальовується, не вдалося — ввід цілий.
 */
const Catalogs: Record<string, Record<string, string>> = {
  en: { 'probe.text': 'Hello' },
  ru: { 'probe.text': 'Привет' },
};

const mounts = vi.fn();

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function ProbePage(): JSX.Element {
  useEffect(() => {
    mounts();
  }, []);
  return <div data-testid="probe">{t('probe.text')}</div>;
}

function show(): void {
  const router = createMemoryRouter(
    [{ path: '/', element: <AppLayout />, children: [{ path: childPath(routes.documentDetail), element: <ProbePage />, handle: routes.documentDetail.handle }] }],
    { initialEntries: ['/documents/12'] },
  );
  render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function switchToRussian(): Promise<void> {
  await act(async () => {
    setLanguage('ru');
    await loadCatalog('ru', 'private');
  });
}

describe('AppLayout: зміна мови й незбережений ввід', () => {
  beforeEach(() => {
    mounts.mockClear();
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes('/api/v1/me')) {
          return json({ userId: 1, userName: 't', language: 'en', permissions: [], isSimulation: false, mustChangePassword: false });
        }
        const match = /\/ui-strings\/([a-z]+)/.exec(url);
        if (match !== null) {
          const lang = match[1] ?? 'en';
          return json({ languageCode: lang, revision: 1, strings: Catalogs[lang] ?? {} });
        }
        return json(null);
      }),
    );
    setLanguage('en');
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    setLanguage('en');
  });

  it('брудний стан не зберігся — сторінка НЕ перемонтовується, ввід цілий', async () => {
    const off = registerUnsavedSource('test-dirty', { hasUnsaved: () => true, flush: async () => false });
    show();
    await screen.findByTestId('probe');
    expect(mounts).toHaveBeenCalledTimes(1);

    await switchToRussian();
    await new Promise((resolve) => setTimeout(resolve, 50));

    // ⛔ Мутація «прибрати remountBlocked» (remount завжди) — mounts == 2, тест червоний.
    expect(mounts).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId('probe').textContent).toBe('Hello');
    off();
  });

  it('брудний стан збережено (flush) — сторінка перемальовується мовою', async () => {
    let dirty = true;
    const off = registerUnsavedSource('test-dirty', {
      hasUnsaved: () => dirty,
      flush: async () => {
        dirty = false;
        return true;
      },
    });
    show();
    await screen.findByTestId('probe');

    await switchToRussian();

    await waitFor(() => expect(screen.getByTestId('probe').textContent).toBe('Привет'));
    expect(mounts).toHaveBeenCalledTimes(2);
    off();
  });

  it('чистий стан — сторінка перемальовується одразу', async () => {
    show();
    await screen.findByTestId('probe');

    await switchToRussian();

    await waitFor(() => expect(screen.getByTestId('probe').textContent).toBe('Привет'));
  });

  // AN-39 / L8-17: доти блокування було мовчазним. Рев'ю AN-39b P2-1: і не авто-remount посеред
  // роботи (набране в редакторі наступної комірки й Undo зникли б) - перемикає кнопка в тості.
  it('не зберіглося — пояснення; сторінка НЕ перемальовується сама, лише кнопкою «Перемкнути зараз»', async () => {
    const show$ = vi.spyOn(notifications, 'show');
    let dirty = true;
    const off = registerUnsavedSource('test-held', { hasUnsaved: () => dirty, flush: async () => false });
    show();
    await screen.findByTestId('probe');

    await switchToRussian();

    await waitFor(() =>
      expect(show$).toHaveBeenCalledWith(expect.objectContaining({ id: 'language-after-save', color: 'statusWarning' })),
    );
    expect(screen.getByTestId('probe').textContent).toBe('Hello');

    // Людина виправила утриману правку - але сама сторінка не перемонтовується.
    dirty = false;
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 1500));
    });
    expect(screen.getByTestId('probe').textContent).toBe('Hello');
    expect(mounts).toHaveBeenCalledTimes(1);

    // Кнопка з тосту - явний вибір людини.
    const toast = show$.mock.calls.find(([options]) => options.id === 'language-after-save')?.[0];
    render(<MantineProvider theme={theme}>{toast?.message}</MantineProvider>);
    fireEvent.click(screen.getByRole('button', { name: /app\.languageSwitchNow|Switch now|Переключить/ }));

    await waitFor(() => expect(screen.getByTestId('probe').textContent).toBe('Привет'));
    off();
    show$.mockRestore();
  });
});
