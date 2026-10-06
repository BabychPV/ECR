import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import type { JSX } from 'react';
import { LoginPage } from '@/pages/LoginPage';
import { resetLanguageCoverage } from '@/shared/ui/LanguageSwitcher';

/**
 * A2-06 п.3: `/login` відкрито при ЖИВОМУ сеансі з разовим паролем (нова
 * вкладка, F5). `GET /public/bootstrap` дає `428` — і людина має опинитися на
 * зміні пароля, а не на формі входу, якою їй користуватися нема чого.
 */
function Where(): JSX.Element {
  return <div data-testid="where">{useLocation().pathname}</div>;
}

function stub(bootstrapStatus: number): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/ui-strings/')) {
        return new Response(JSON.stringify({ languageCode: 'a206', revision: 1, strings: {} }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      if (url.includes('/public/bootstrap')) {
        const body =
          bootstrapStatus === 200
            ? { productVersion: '1.0.0', languages: [], windowsSignInEnabled: true, localSignInEnabled: true }
            : { title: 'x', status: bootstrapStatus, errorCode: 'ECR-PWD-0428', correlationId: 'c' };
        return new Response(JSON.stringify(body), {
          status: bootstrapStatus,
          headers: { 'Content-Type': bootstrapStatus === 200 ? 'application/json' : 'application/problem+json' },
        });
      }
      throw new Error(`Непередбачений запит: ${url}`);
    }),
  );
}

function show(): void {
  render(
    <MantineProvider>
      <MemoryRouter initialEntries={['/login']}>
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route path="*" element={null} />
        </Routes>
        <Where />
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  resetLanguageCoverage();
  localStorage.clear();
});

describe('LoginPage: 428 на bootstrap', () => {
  it('веде на /change-password', async () => {
    localStorage.setItem('uiLanguage', 'a206');
    stub(428);
    show();

    await waitFor(() => expect(screen.getByTestId('where').textContent).toBe('/change-password'));
  });

  it.each([200, 503])('%i — лишається на /login', async (status) => {
    localStorage.setItem('uiLanguage', 'a206');
    stub(status);
    show();

    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([u]) => String(u).includes('/public/bootstrap'))).toBe(true));
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(screen.getByTestId('where').textContent).toBe('/login');
  });
});
