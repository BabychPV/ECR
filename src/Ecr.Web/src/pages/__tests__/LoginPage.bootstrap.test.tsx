import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import type { JSX } from 'react';
import { LoginPage } from '@/pages/LoginPage';
import { resetLanguageCoverage } from '@/shared/ui/LanguageSwitcher';
import type { PublicBootstrap } from '@/features/public/api';

/**
 * Екран входу читає `GET /api/v1/public/bootstrap` (`BE-07`).
 *
 * ⛔ Перевіряється не «хук працює», а три твердження про ПОВЕДІНКУ екрана:
 * спосіб входу, якого сервер не має, не показується; збій допоміжного запиту
 * НЕ забирає форму входу; і жодне поле користувача не їде на анонімну адресу.
 * Останнє — клієнтська половина межі розкриття: серверна половина (відповідь
 * однакова для наявного й неіснуючого логіна) живе в
 * `tests/Ecr.Api.Tests/PublicBootstrapTests.cs`.
 */
function Shell({ children }: { children: JSX.Element }): JSX.Element {
  return (
    <MantineProvider>
      <MemoryRouter>{children}</MemoryRouter>
    </MantineProvider>
  );
}

const STRINGS: Record<string, string> = {
  'login.title': 'Environmental Compliance Reporting',
  'login.windows': 'Sign in with Windows',
  'login.or': 'or',
  'login.user': 'User name',
  'login.password': 'Password',
  'login.submit': 'Sign in',
  'login.hint': 'Use your Windows account or a local one',
};

/**
 * Каталог відповідає успішно, bootstrap — заданим тілом (або відмовою).
 *
 * ⚠ Маршрутизація за адресою, а не «відповідати всім однаково»: підміна, яка
 * віддає каталог у відповідь на bootstrap, перевіряла б збіг випадковостей.
 */
function stubFetch(lang: string, bootstrap: PublicBootstrap | 'fail'): ReturnType<typeof vi.fn> {
  const calls = vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input);

    // ⚠ `kz` — з власним перекладом заголовка: мова без жодного перекладу на
    // екрані входу більше не пропонується (`R-16`).
    if (url.includes('/ui-strings/kz')) {
      return new Response(
        JSON.stringify({
          languageCode: 'kz',
          revision: 1,
          strings: { ...STRINGS, 'login.title': 'Экологиялық есептілік' },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      );
    }

    if (url.includes('/ui-strings/')) {
      return new Response(JSON.stringify({ languageCode: lang, revision: 1, strings: STRINGS }), {
        status: 200,
        headers: { 'Content-Type': 'application/json', ETag: `"public-${lang}-1"` },
      });
    }

    if (url.includes('/public/bootstrap')) {
      if (bootstrap === 'fail') {
        return new Response(
          JSON.stringify({
            title: 'Недоступно',
            status: 503,
            errorCode: 'HTTP-503',
            correlationId: 'cid-bootstrap-1',
          }),
          { status: 503, headers: { 'Content-Type': 'application/json' } },
        );
      }

      return new Response(JSON.stringify(bootstrap), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }

    throw new Error(`Непередбачений запит у тесті: ${url}`);
  });

  vi.stubGlobal('fetch', calls);
  return calls;
}

/** Відповідь сервера з одним увімкненим способом входу. */
function bootstrapOf(overrides: Partial<PublicBootstrap>): PublicBootstrap {
  return {
    productVersion: '1.0.0',
    languages: [],
    windowsSignInEnabled: true,
    localSignInEnabled: true,
    ...overrides,
  };
}

afterEach(() => {
  vi.unstubAllGlobals();
  resetLanguageCoverage();
  localStorage.clear();
});

describe('LoginPage: публічні дані екрана входу (BE-07)', () => {
  // ⚠ Унікальна мова на КОЖЕН тест: `loaded`/`failed` у `shared/i18n` —
  // модульний стан, що переживає окремі `it()`. Спільний ключ дав би зелене
  // від чужого стану, а не від коду, який тест перевіряє.

  it('доменний вхід вимкнено на сервері — кнопки немає, локальна форма є', async () => {
    localStorage.setItem('uiLanguage', 'be07-a');
    stubFetch('be07-a', bootstrapOf({ windowsSignInEnabled: false }));

    render(
      <Shell>
        <LoginPage />
      </Shell>,
    );

    await screen.findByLabelText('User name');

    // ⛔ Головне заперечення: кнопка, яка гарантовано дасть 401, не малюється.
    await waitFor(() => {
      expect(screen.queryByRole('button', { name: 'Sign in with Windows' })).toBeNull();
    });

    expect(screen.getByRole('button', { name: 'Sign in' })).toBeDefined();

    // Розділювач «or» теж зникає: розділяти лишилося нічого.
    expect(screen.queryByText('or')).toBeNull();
  });

  it('локальний вхід вимкнено — полів немає, кнопка домену лишається', async () => {
    localStorage.setItem('uiLanguage', 'be07-b');
    stubFetch('be07-b', bootstrapOf({ localSignInEnabled: false }));

    render(
      <Shell>
        <LoginPage />
      </Shell>,
    );

    await screen.findByRole('button', { name: 'Sign in with Windows' });

    await waitFor(() => {
      expect(screen.queryByLabelText('User name')).toBeNull();
    });

    expect(screen.queryByLabelText('Password')).toBeNull();
  });

  it('bootstrap відмовив — форма входу лишається повною, без червоного', async () => {
    // ⛔ Найважливіший із трьох. Допоміжний запит не має права забрати єдиний
    // екран, через який узагалі заходять у систему: збій мережі на ньому мусить
    // давати обидва способи входу, а не жодного.
    localStorage.setItem('uiLanguage', 'be07-c');
    stubFetch('be07-c', 'fail');

    render(
      <Shell>
        <LoginPage />
      </Shell>,
    );

    expect(await screen.findByRole('button', { name: 'Sign in with Windows' })).toBeDefined();
    expect(screen.getByLabelText('User name')).toBeDefined();
    expect(screen.getByLabelText('Password')).toBeDefined();

    // Відмова допоміжного запиту НЕ показується: діяти на неї користувач не
    // може, а червона смуга над робочою формою лише лякає.
    expect(screen.queryByRole('alert')).toBeNull();
    expect(document.body.textContent).not.toMatch(/⟦[^⟧]*⟧/);
  });

  it('версія продукту показується як є, а перемикач мови — з реєстру сервера', async () => {
    localStorage.setItem('uiLanguage', 'be07-d');
    stubFetch(
      'be07-d',
      bootstrapOf({
        productVersion: '2.4.1',
        languages: [
          { code: 'be07-d', nameNative: 'English', isDefault: true },
          { code: 'kz', nameNative: 'Қазақша', isDefault: false },
        ],
      }),
    );

    render(
      <Shell>
        <LoginPage />
      </Shell>,
    );

    expect(await screen.findByText('2.4.1')).toBeDefined();

    // ⛔ Пункти беруться з відповіді сервера, а не з константи бандла
    // (`ФВ-14.9`): список у коді зробив би вимогу «додавання мови — запис у
    // реєстр» невиконуваною саме на тому екрані, де мову й обирають.
    const picker = await screen.findByRole('combobox', { name: 'Interface language' });
    const options = Array.from(picker.querySelectorAll('option')).map((o) => o.textContent);

    expect(options).toEqual(['English', 'Қазақша']);
  });

  it('R-16: мова без жодного перекладу на екрані входу не пропонується', async () => {
    localStorage.setItem('uiLanguage', 'be07-f');
    stubFetch(
      'be07-f',
      bootstrapOf({
        productVersion: '2.4.2',
        languages: [
          { code: 'be07-f', nameNative: 'English', isDefault: true },
          // ⚠ Той самий зріз, що й у мови за замовчуванням: перекладу немає.
          { code: 'ru', nameNative: 'Русский', isDefault: false },
        ],
      }),
    );

    render(
      <Shell>
        <LoginPage />
      </Shell>,
    );

    expect(await screen.findByText('2.4.2')).toBeDefined();
    await new Promise((resolve) => setTimeout(resolve, 50));

    // ⛔ Мутація «повернути `bootstrap.languages`» показує перемикач із
    // «Русский», за яким той самий англійський інтерфейс.
    expect(screen.queryByRole('combobox', { name: 'Interface language' })).toBeNull();
  });

  it('на анонімну адресу не їде ні імені користувача, ні пароля', async () => {
    // ⛔ Клієнтська половина межі розкриття (`D15-14`). Адреса має бути голою:
    // будь-який параметр із логіном перетворив би анонімний ендпоінт на місце,
    // де є ЩО розрізняти, — і наступний крок «а поверни для нього лічильник
    // спроб» став би природним.
    localStorage.setItem('uiLanguage', 'be07-e');
    const calls = stubFetch('be07-e', bootstrapOf({}));

    render(
      <Shell>
        <LoginPage />
      </Shell>,
    );

    await screen.findByLabelText('User name');

    const bootstrapCalls = calls.mock.calls
      .map((call) => String(call[0]))
      .filter((url) => url.includes('/public/bootstrap'));

    expect(bootstrapCalls.length).toBeGreaterThan(0);

    for (const url of bootstrapCalls) {
      expect(url).toBe('/api/v1/public/bootstrap');
    }

    // ⚠ І метод, і тіло: `GET` без тіла — єдина форма, у якій нічого не можна
    // передати «непомітно».
    const init = calls.mock.calls.find((call) =>
      String(call[0]).includes('/public/bootstrap'),
    )?.[1] as RequestInit | undefined;

    expect(init?.method ?? 'GET').toBe('GET');
    expect(init?.body ?? null).toBeNull();
  });
});
