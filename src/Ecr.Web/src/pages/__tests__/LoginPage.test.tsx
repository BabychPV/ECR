import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import type { JSX } from 'react';
import { LoginPage } from '@/pages/LoginPage';

/**
 * Сторінка входу при недоступному каталозі перекладів (`D-138`, `A7-33`).
 *
 * ⛔ Раніше catch-блок `loadCatalog` клав ПОРОЖНІЙ каталог у той самий
 * `loaded`, яким користується успішне завантаження, і жодним чином не
 * позначав відмову. `isCatalogResolved` тому поверталося `true` в обох
 * випадках, `LoginPage` малювала форму входу, а `t('login.title')` та інші
 * виклики — самі позначені ключі (`⟦login.title⟧`) замість написів, бо
 * каталог для поточної мови був порожній.
 *
 * ⚠ Тест б'є мережевий запит `/api/v1/ui-strings/...` (той самий шлях, що й
 * у продакшн-коді, без підміни на щось простіше) і перевіряє, що сторінка
 * показує `<ErrorAlert>` замість форми — і що на екрані немає ЖОДНОГО
 * позначеного ключа.
 */
function Shell({ children }: { children: JSX.Element }): JSX.Element {
  return (
    <MantineProvider>
      <MemoryRouter>{children}</MemoryRouter>
    </MantineProvider>
  );
}

/** Каталог перекладів відповідає відмовою (не-2xx). */
function failingCatalog(status = 500): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      // ⚠ Лише шлях каталогу відмовляє: якщо колись цей тест почне ловити
      // інший запит (наприклад, `apiFetch` при сабміті), відмова мала б
      // стосуватися ЛИШЕ каталогу, а не всього фетчу підряд.
      if (String(input).includes('/ui-strings/')) {
        return new Response(
          JSON.stringify({
            title: 'Недоступно',
            status,
            errorCode: `HTTP-${status}`,
            correlationId: 'cid-catalog-1',
          }),
          { status, headers: { 'Content-Type': 'application/json' } },
        );
      }

      throw new Error(`Непередбачений запит у тесті: ${String(input)}`);
    }),
  );
}

/** Каталог перекладів відповідає успішно. */
function workingCatalog(strings: Record<string, string>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(JSON.stringify({ languageCode: 'ok', revision: 1, strings }), {
          status: 200,
          headers: { 'Content-Type': 'application/json', ETag: '"public-ok-1"' },
        }),
      ),
    ),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('LoginPage: каталог перекладів не завантажився', () => {
  it('D-138: показує ErrorAlert замість форми і без жодного ⟦...⟧', async () => {
    // ⚠ Унікальна мова на тест: `loaded`/`failed` у `shared/i18n` — модульний
    // стан, що переживає окремі `it()`. Той самий ключ в іншому тесті дав би
    // хибний позитив від чужого стану, а не від коду, який тест перевіряє.
    localStorage.setItem('uiLanguage', 'fail-lang');
    failingCatalog();

    render(
      <Shell>
        <LoginPage />
      </Shell>,
    );

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('Переклади інтерфейсу не завантажилися');

    // ⛔ Головне заперечення: жодного позначеного ключа на екрані. До
    // виправлення тут стояли б `⟦login.title⟧`, `⟦login.submit⟧` тощо.
    expect(document.body.textContent).not.toMatch(/⟦[^⟧]*⟧/);

    // ⛔ Форма входу НЕ малюється: жодного поля вводу на екрані.
    expect(document.querySelectorAll('input').length).toBe(0);
  });

  it('контрольний прогін: справний каталог показує форму, а не ErrorAlert', async () => {
    localStorage.setItem('uiLanguage', 'ok');
    workingCatalog({
      'login.title': 'Environmental Compliance Reporting',
      'login.windows': 'Sign in with Windows',
      'login.or': 'or',
      'login.user': 'User name',
      'login.password': 'Password',
      'login.submit': 'Sign in',
      'login.hint': 'Use your Windows account or a local one',
    });

    render(
      <Shell>
        <LoginPage />
      </Shell>,
    );

    expect(await screen.findByText('Environmental Compliance Reporting')).toBeDefined();
    expect(screen.queryByRole('alert')).toBeNull();
    expect(document.body.textContent).not.toMatch(/⟦[^⟧]*⟧/);
  });
});
