import { describe, it, expect, vi, afterEach } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import type { JSX } from 'react';
import { LoginPage } from '@/pages/LoginPage';

/**
 * U-9 (аудит 2026-11, UX-16): підказка «Caps Lock увімкнено» під полем пароля, поки фокус у ньому. Невдала спроба з
 * Caps Lock давала загальне «невірний логін або пароль», а кілька таких спроб блокують обліковий запис.
 *
 * ⚠ Стан Caps Lock — `KeyboardEvent.getModifierState('CapsLock')`; jsdom бере його з `modifierCapsLock` в init події.
 * Мутація: прибрати `onKeyUp`/`onKeyDown` з поля — перший тест червоний.
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
            languageCode: 'caps-ok',
            revision: 1,
            strings: {
              'login.title': 'Environmental Compliance Reporting',
              'login.windows': 'Sign in with Windows',
              'login.or': 'or',
              'login.user': 'User name',
              'login.password': 'Password',
              'login.submit': 'Sign in',
              'login.hint': 'Use your Windows account or a local one',
              'login.capsLockOn': 'Caps Lock is on',
            },
          }),
          { status: 200, headers: { 'Content-Type': 'application/json', ETag: '"public-caps-1"' } },
        ),
      ),
    ),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

async function passwordField(): Promise<HTMLElement> {
  localStorage.setItem('uiLanguage', 'caps-ok');
  workingCatalog();
  render(
    <Shell>
      <LoginPage />
    </Shell>,
  );
  return screen.findByLabelText('Password');
}

describe('LoginPage: підказка Caps Lock (U-9, UX-16)', () => {
  it('Caps Lock увімкнено — підказка під полем, пов\'язана з ним; blur — підказки немає', async () => {
    const field = await passwordField();
    field.focus();

    fireEvent(field, new KeyboardEvent('keyup', { key: 'A', bubbles: true, modifierCapsLock: true } as KeyboardEventInit));

    const hint = await screen.findByText('Caps Lock is on');
    // Читалка чує підказку разом із полем.
    // ⚠ Mantine `PasswordInput` сам `aria-describedby` для `description` не ставить — перевіряється явний зв'язок.
    const describedBy = screen.getByLabelText('Password').getAttribute('aria-describedby') ?? '';
    expect(describedBy.split(' ')).toContain(hint.id);

    fireEvent.blur(field);
    expect(screen.queryByText('Caps Lock is on')).toBeNull();
  });

  it('Caps Lock вимкнено — підказки немає', async () => {
    const field = await passwordField();
    field.focus();

    fireEvent(field, new KeyboardEvent('keyup', { key: 'a', bubbles: true, modifierCapsLock: false } as KeyboardEventInit));

    expect(screen.queryByText('Caps Lock is on')).toBeNull();
  });
});
