import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { LoginPage } from '@/pages/LoginPage';
import { apiFetch, loginUrl, setLoginRedirect, type LoginReason } from '@/api/client';

/**
 * Сервер обірвав сесію штампом безпеки (`SecurityStampMiddleware`): змінилися
 * гранти ролі, пароль чи блокування. Наступний запит отримує `401` із тілом
 * `problem+json` і кодом `ECR-AUTH-0401`. Людина має побачити на вході
 * пояснення, а не мовчазний переліт.
 *
 * ⚠ Дзеркало — `401` без тіла: так відповідає cookie-схема, коли людина просто
 * не входила (`OnRedirectToLogin`). Там пояснення бути НЕ повинно — інакше
 * кожен перший вхід починався б зі «сеанс завершено».
 *
 * ⚠ Ланцюг ходить справжнім шляхом: `apiFetch` → обробник редиректу → адреса,
 * яку будує `loginUrl` (та сама, що в продакшн-редиректі) → `LoginPage`.
 */
const SIGN_IN_REQUIRED = 'You are not signed in or your session has ended: sign in again.';

const baseStrings: Record<string, string> = {
  'login.title': 'Environmental Compliance Reporting',
  'login.windows': 'Sign in with Windows',
  'login.or': 'or',
  'login.user': 'User name',
  'login.password': 'Password',
  'login.submit': 'Sign in',
  'login.hint': 'Use your Windows account or a local one',
  'err.ECR-AUTH-0401.signInRequired': SIGN_IN_REQUIRED,
};

/** Каталог — для `/ui-strings/`, для `/api/v1/me` — задана відповідь `401`. */
function server(strings: Record<string, string>, unauthorized: () => Response): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.startsWith('/api/v1/me')) return unauthorized();
      return new Response(JSON.stringify({ languageCode: 'x', revision: 1, strings }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

const stampRejected = (): Response =>
  new Response(
    JSON.stringify({
      title: 'Sign in to continue.',
      status: 401,
      errorCode: 'ECR-AUTH-0401',
      correlationId: 'cid-stamp',
    }),
    { status: 401, headers: { 'Content-Type': 'application/problem+json' } },
  );

const notSignedIn = (): Response => new Response('', { status: 401 });

/** Запит отримує 401 — повертає адресу входу, куди повів би клієнт. */
async function redirectedTo(): Promise<string> {
  let target: string | null = null;
  setLoginRedirect((from: string, reason?: LoginReason) => {
    target = loginUrl(from, reason);
  });
  await apiFetch('/api/v1/me').catch(() => undefined);
  if (target === null) throw new Error('клієнт не повів на вхід');
  return target;
}

/** Банер причини (Mantine Alert сам ставить role="alert", тож шукаємо за атрибутом). */
async function waitForBanner(): Promise<Element> {
  await screen.findByText('Environmental Compliance Reporting');
  const banner = document.querySelector('[data-login-reason="session-invalidated"]');
  if (banner === null) throw new Error('банера причини немає');
  return banner;
}

function renderLogin(entry: string): void {
  render(
    <MantineProvider>
      <MemoryRouter initialEntries={[entry]}>
        <LoginPage />
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  setLoginRedirect(() => {});
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('LoginPage: сесія втратила чинність (401 ECR-AUTH-0401)', () => {
  it('штамп безпеки → на вході пояснення з каталогу', async () => {
    localStorage.setItem('uiLanguage', 'stamp-a');
    server(baseStrings, stampRejected);

    const target = await redirectedTo();
    expect(target).toContain('/login?from=');

    renderLogin(target);

    const banner = await waitForBanner();
    expect(banner.textContent).toContain(SIGN_IN_REQUIRED);
    expect(document.body.textContent).not.toMatch(/⟦[^⟧]*⟧/);
  });

  it('дзеркало: 401 без тіла (не входив) — пояснення немає', async () => {
    localStorage.setItem('uiLanguage', 'stamp-b');
    server(baseStrings, notSignedIn);

    const target = await redirectedTo();
    renderLogin(target);

    expect(await screen.findByText('Environmental Compliance Reporting')).toBeDefined();
    expect(document.querySelector('[data-login-reason]')).toBeNull();
    expect(document.body.textContent).not.toContain(SIGN_IN_REQUIRED);
  });
});
