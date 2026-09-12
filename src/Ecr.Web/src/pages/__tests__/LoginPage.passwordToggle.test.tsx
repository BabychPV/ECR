import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import type { JSX } from 'react';
import { LoginPage } from '@/pages/LoginPage';

/**
 * Тумблер видимості пароля на екрані входу (`Q-260`).
 *
 * ⛔ Mantine `PasswordInput` ставить на кнопку-тумблер `aria-hidden="true"` і
 * `tabIndex={-1}` за замовчуванням, доки `visibilityToggleButtonProps` цього
 * не перекриє — кнопка існувала лише для миші, і жоден компонентний тест
 * раніше цього не перевіряв.
 */
function Shell({ children }: { children: JSX.Element }): JSX.Element {
  return (
    <MantineProvider>
      <MemoryRouter>{children}</MemoryRouter>
    </MantineProvider>
  );
}

function workingCatalog(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(
          JSON.stringify({
            languageCode: 'toggle-ok',
            revision: 1,
            strings: {
              'login.title': 'Environmental Compliance Reporting',
              'login.windows': 'Sign in with Windows',
              'login.or': 'or',
              'login.user': 'User name',
              'login.password': 'Password',
              'login.submit': 'Sign in',
              'login.hint': 'Use your Windows account or a local one',
            },
          }),
          { status: 200, headers: { 'Content-Type': 'application/json', ETag: '"public-toggle-1"' } },
        ),
      ),
    ),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('LoginPage: тумблер видимості пароля (Q-260)', () => {
  it('доступний з клавіатури і має ім\'я для читалки', async () => {
    localStorage.setItem('uiLanguage', 'toggle-ok');
    workingCatalog();

    render(
      <Shell>
        <LoginPage />
      </Shell>,
    );

    await screen.findByLabelText('Password');

    const toggle = screen.getByRole('button', { name: 'Toggle password visibility' });

    // ⛔ Головне заперечення: за замовчуванням Mantine ставить це саме на
    // `true` — без фіксу елемент цілком випадає з дерева доступності, і
    // `getByRole('button', { name: ... })` вище впав би сам по собі.
    expect(toggle.getAttribute('aria-hidden')).not.toBe('true');
    expect(toggle.getAttribute('tabindex')).toBe('0');
  });
});
