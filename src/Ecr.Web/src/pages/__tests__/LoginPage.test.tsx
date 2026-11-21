import { describe, it, expect, vi, afterEach } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
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
    expect(alert.textContent).toContain('Interface texts could not be loaded');

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

  it('T1-15 (г): заголовок сторінки входу — h1, а вкладка названа системою', async () => {
    localStorage.setItem('uiLanguage', 'title-lang');
    document.title = 'ECR Web';
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

    const heading = await screen.findByRole('heading', { level: 1 });
    expect(heading.textContent).toBe('Environmental Compliance Reporting');
    expect(document.title).toBe('Environmental Compliance Reporting · ECR');
  });

  it('T1-15 (г): при збої каталогу заголовок вкладки не отримує позначеного ключа', async () => {
    localStorage.setItem('uiLanguage', 'title-fail-lang');
    document.title = 'ECR Web';
    failingCatalog();

    render(
      <Shell>
        <LoginPage />
      </Shell>,
    );

    await screen.findByRole('alert');
    expect(document.title).not.toContain('⟦');
  });
});

/**
 * Каталог віддав `429` (PSS-29, e2e RC #988): межа частоти анонімних читань клала екран входу всім за однією
 * адресою, і людина бачила «Check the network connection» — пораду лагодити справну мережу.
 */
describe('LoginPage: сервер зайнятий (429 на каталозі)', () => {
  /** Каталог відповідає голим 429 (як `LoginRateLimiting.RejectBare`) задану кількість разів, далі — успіхом. */
  function busyCatalog(retryAfter: string, times: number): ReturnType<typeof vi.fn> {
    let left = times;
    const calls = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (!url.includes('/ui-strings/')) throw new Error(`Непередбачений запит у тесті: ${url}`);

      if (left > 0) {
        left -= 1;
        return new Response(null, { status: 429, headers: { 'Retry-After': retryAfter } });
      }

      return new Response(
        JSON.stringify({ languageCode: 'x', revision: 1, strings: { 'login.title': 'Environmental Compliance Reporting' } }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      );
    });
    vi.stubGlobal('fetch', calls);
    return calls;
  }

  /** Скільки разів просили саме каталог (bootstrap іде тим самим `fetch`). */
  function catalogCalls(calls: ReturnType<typeof vi.fn>): number {
    return calls.mock.calls.filter(([input]) => String(input).includes('/ui-strings/')).length;
  }

  it('повтори вичерпано — «сервер зайнятий, спробуйте за N с», без «перевірте мережу» і з кнопкою повтору', async () => {
    localStorage.setItem('uiLanguage', 'busy-lang');
    busyCatalog('120', Number.MAX_SAFE_INTEGER);

    render(
      <Shell>
        <LoginPage />
      </Shell>,
    );

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('The server is busy');
    expect(alert.textContent).toContain('Try again in 120 s');
    expect(document.body.textContent).not.toContain('Check the network connection');
    expect(document.body.textContent).not.toMatch(/⟦[^⟧]*⟧/);
    expect(screen.getByRole('button', { name: 'Try again' })).toBeDefined();
  });

  it('429 з коротким Retry-After — «повторюємо» під спінером, потім форма входу сама', async () => {
    localStorage.setItem('uiLanguage', 'busy-then-ok');
    const calls = busyCatalog('1', 1);

    render(
      <Shell>
        <LoginPage />
      </Shell>,
    );

    expect(await screen.findByText('The server is busy. Retrying automatically…')).toBeDefined();
    expect(await screen.findByText('Environmental Compliance Reporting', {}, { timeout: 4000 })).toBeDefined();
    expect(screen.queryByRole('alert')).toBeNull();
    expect(catalogCalls(calls)).toBe(2);
  });

  it('кнопка «Try again» повторює завантаження без перезавантаження сторінки', async () => {
    localStorage.setItem('uiLanguage', 'busy-manual');
    const calls = busyCatalog('120', 1);

    render(
      <Shell>
        <LoginPage />
      </Shell>,
    );

    const retry = await screen.findByRole('button', { name: 'Try again' });
    fireEvent.click(retry);

    expect(await screen.findByText('Environmental Compliance Reporting')).toBeDefined();
    expect(catalogCalls(calls)).toBe(2);
  });
});
