import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import type { JSX } from 'react';
import { LoginPage } from '@/pages/LoginPage';

/**
 * Після входу — назад на `from`, але лише на внутрішній шлях (захист від
 * відкритого редиректу). Тест іде справжнім шляхом: форма → `POST` входу →
 * `navigate` у роутері, і дивиться, де опинився роутер.
 */
const strings: Record<string, string> = {
  'login.title': 'Environmental Compliance Reporting',
  'login.windows': 'Sign in with Windows',
  'login.or': 'or',
  'login.user': 'User name',
  'login.password': 'Password',
  'login.submit': 'Sign in',
  'login.hint': 'Hint',
};

function Where(): JSX.Element {
  const location = useLocation();
  return <div data-testid="where">{location.pathname + location.search}</div>;
}

function server(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).startsWith('/api/v1/login/local')) return new Response(null, { status: 204 });
      if (String(input).includes('bootstrap')) {
        return new Response(JSON.stringify({ windowsSignInEnabled: false, localSignInEnabled: true, languages: [] }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      return new Response(JSON.stringify({ languageCode: 'x', revision: 1, strings }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

async function signInFrom(from: string, lang: string): Promise<string> {
  localStorage.setItem('uiLanguage', lang);
  server();
  render(
    <MantineProvider>
      <MemoryRouter initialEntries={[`/login?from=${encodeURIComponent(from)}`]}>
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route path="*" element={<Where />} />
        </Routes>
      </MemoryRouter>
    </MantineProvider>,
  );
  fireEvent.click(await screen.findByRole('button', { name: 'Sign in' }));
  return (await screen.findByTestId('where')).textContent ?? '';
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('LoginPage: повернення на попередню адресу', () => {
  it('from=/documents/1 → туди', async () => {
    expect(await signInFrom('/documents/1', 'ret-a')).toBe('/documents/1');
  });

  it.each([
    ['//evil.com', 'ret-b'],
    ['https://evil.com', 'ret-c'],
    ['/\\evil.com', 'ret-d'],
  ])('from=%s → на /', async (from, lang) => {
    expect(await signInFrom(from, lang)).toBe('/');
  });
});
