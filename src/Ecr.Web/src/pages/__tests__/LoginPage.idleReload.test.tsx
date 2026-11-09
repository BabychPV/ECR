import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { LoginPage } from '@/pages/LoginPage';
import { apiFetch, IDLE_SIGNOUT_MARKER_KEY, IDLE_TIMEOUT_MESSAGE_KEY, setLoginRedirect } from '@/api/client';

/**
 * ✎ Борг 09.11 (D-471): сервер віддає `401 idleTimeout` лише на ПЕРШИЙ запит із простроченим cookie й стирає
 * його; перезавантаження далі отримує звичайний `401`, і вхід губив «вас виведено через N хв бездіяльності».
 * Клієнт запам'ятовує вихід у `sessionStorage` вкладки, а сторінка входу без причини в адресі читає позначку.
 *
 * Мутація (локально): прибрати `rememberIdleSignOut` з гілки `idle` у `apiFetch` — перший тест червоніє.
 */
const IDLE_TEXT = 'You were signed out after 15 min of inactivity.';

const strings: Record<string, string> = {
  'login.title': 'Environmental Compliance Reporting',
  'login.windows': 'Sign in with Windows',
  'login.or': 'or',
  'login.user': 'User name',
  'login.password': 'Password',
  'login.submit': 'Sign in',
  'login.hint': 'Use your Windows account or a local one',
  'login.idleSignedOut': 'You were signed out after {minutes} min of inactivity.',
  'login.idleSignedOutNoMinutes': 'You were signed out after a period of inactivity.',
};

function server(me: () => Response): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.startsWith('/api/v1/me')) return me();
      return new Response(JSON.stringify({ languageCode: 'x', revision: 1, strings }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

const idle401 = (): Response =>
  new Response(
    JSON.stringify({
      title: 'Signed out.',
      status: 401,
      errorCode: 'ECR-AUTH-0401',
      messageKey: IDLE_TIMEOUT_MESSAGE_KEY,
      idleMinutes: 15,
      correlationId: 'cid-idle',
    }),
    { status: 401, headers: { 'Content-Type': 'application/problem+json' } },
  );

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
  sessionStorage.clear();
});

describe('LoginPage: вихід після бездіяльності переживає перезавантаження', () => {
  it('idle 401, потім перезавантаження (адреса без причини) — текст із межею лишається', async () => {
    localStorage.setItem('uiLanguage', 'idle-a');
    server(idle401);
    setLoginRedirect(() => {});
    await apiFetch('/api/v1/me').catch(() => undefined);

    // Перезавантаження: сервер уже стер cookie — адреса входу без `reason`.
    renderLogin('/login?from=%2Fdocuments');

    await screen.findByText('Environmental Compliance Reporting');
    const banner = document.querySelector('[data-login-reason="idle"]');
    expect(banner?.textContent).toContain(IDLE_TEXT);
  });

  it('дзеркало: без позначки і без причини — тексту немає', async () => {
    localStorage.setItem('uiLanguage', 'idle-b');
    server(() => new Response('', { status: 401 }));

    renderLogin('/login?from=%2Fdocuments');

    await screen.findByText('Environmental Compliance Reporting');
    expect(document.querySelector('[data-login-reason]')).toBeNull();
  });

  it('давня позначка (понад 12 год) не показується', async () => {
    localStorage.setItem('uiLanguage', 'idle-c');
    server(() => new Response('', { status: 401 }));
    sessionStorage.setItem(
      IDLE_SIGNOUT_MARKER_KEY,
      JSON.stringify({ minutes: 15, at: Date.now() - 13 * 60 * 60 * 1000 }),
    );

    renderLogin('/login?from=%2Fdocuments');

    await screen.findByText('Environmental Compliance Reporting');
    expect(document.querySelector('[data-login-reason]')).toBeNull();
  });
});
