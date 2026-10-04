import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useEffect, type JSX } from 'react';
import { LanguageSwitcher, resetLanguageCoverage } from '@/shared/ui/LanguageSwitcher';
import { registerUnsavedSource } from '@/shared/ui/unsavedSources';
import { useCatalog } from '@/shared/i18n/useCatalog';
import { language, loadCatalog, setLanguage, t } from '@/shared/i18n';

/**
 * T4-05 (тестувальник, прохід №4, P2): зміна мови при брудній сторінці.
 *
 * ⛔ Було: у таблиці відхилена сервером правка («Retry save»), меню → Қазақша — перемикач і
 * оболонка вже казахською, а вміст сторінки англійською (remount `AppLayout` чекає збереження,
 * T3-02), і жодного пояснення. Стало: спершу зберегти; не вдалося — мова НЕ змінюється, перемикач
 * лишається на поточній, а сповіщення каже зберегти чи скасувати зміни.
 */
function routes(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      const body = url.includes('/api/v1/languages')
        ? [
            { code: 'en', nameNative: 'English', isDefault: true },
            { code: 'kz', nameNative: 'Қазақша', isDefault: false },
          ]
        : url.includes('/ui-strings/kz')
          ? { languageCode: 'kz', revision: 1, strings: { 'test.marker': 'kz-loaded' } }
          : { languageCode: 'en', revision: 1, strings: { 'test.marker': 'en-loaded' } };

      return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

function Page(): JSX.Element {
  useCatalog();

  useEffect(() => {
    void loadCatalog('en', 'private');
  }, []);

  return (
    <>
      <LanguageSwitcher />
      <p>{t('test.marker')}</p>
    </>
  );
}

function show(): void {
  render(
    <MantineProvider>
      <Notifications />
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <Page />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

let off: (() => void) | null = null;

afterEach(() => {
  off?.();
  off = null;
  notifications.clean();
  vi.unstubAllGlobals();
  resetLanguageCoverage();
  localStorage.clear();
  setLanguage('en');
});

describe('Перемикач мови й незбережений ввід (T4-05)', () => {
  it('зберегти не вдалося — мова не змінюється, перемикач на поточній, є пояснення', async () => {
    const flush = vi.fn(async () => false);
    off = registerUnsavedSource('test-rejected', { hasUnsaved: () => true, flush });
    routes();
    show();

    expect(await screen.findByText('en-loaded')).toBeDefined();

    const select = (await screen.findByLabelText('⟦profile.language⟧')) as HTMLSelectElement;
    fireEvent.change(select, { target: { value: 'kz' } });

    // ⛔ Мутація «прибрати перевірку брудного стану» (одразу `applyLanguage`) — сповіщення немає,
    // мова стає `kz`: тест червоний.
    expect(await screen.findByText('⟦profile.languageUnsavedBlocked⟧')).toBeDefined();
    expect(flush).toHaveBeenCalledTimes(1);
    expect(language()).toBe('en');
    expect(localStorage.getItem('uiLanguage')).not.toBe('kz');
    expect(select.value).toBe('en');
    expect(screen.queryByText('kz-loaded')).toBeNull();
  });

  it('збереження впало (відхилений промис) — мова не змінюється, є пояснення', async () => {
    off = registerUnsavedSource('test-throwing', {
      hasUnsaved: () => true,
      flush: () => Promise.reject(new Error('network down')),
    });
    routes();
    show();

    expect(await screen.findByText('en-loaded')).toBeDefined();

    const select = (await screen.findByLabelText('⟦profile.language⟧')) as HTMLSelectElement;
    fireEvent.change(select, { target: { value: 'kz' } });

    // ⛔ Мутація «прибрати .catch після flushUnsaved» — відмова лишається необробленою,
    // сповіщення немає: тест червоний.
    expect(await screen.findByText('⟦profile.languageUnsavedBlocked⟧')).toBeDefined();
    expect(language()).toBe('en');
    expect(select.value).toBe('en');
  });

  it('незбережене вдалося зберегти — мова змінюється без сповіщення', async () => {
    let dirty = true;
    off = registerUnsavedSource('test-dirty', {
      hasUnsaved: () => dirty,
      flush: async () => {
        dirty = false;
        return true;
      },
    });
    routes();
    show();

    expect(await screen.findByText('en-loaded')).toBeDefined();

    fireEvent.change(await screen.findByLabelText('⟦profile.language⟧'), { target: { value: 'kz' } });

    expect(await screen.findByText('kz-loaded')).toBeDefined();
    expect(language()).toBe('kz');
    await waitFor(() => expect(screen.queryByText('⟦profile.languageUnsavedBlocked⟧')).toBeNull());
  });
});
