import { describe, it, expect, afterEach, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { SecurityPage } from '@/pages/admin/SecurityPage';

/**
 * Аудит 2026-09-16, §10.8: ОДИН спільний `useMutation` на весь перелік
 * керував станом «завантаження» для всіх рядків одночасно.
 *
 * ⛔ `disabled={… || alerts.isPending}` стоїть на перемикачі КОЖНОГО рядка, а
 * `alerts` — одна мутація на всю сторінку. Перемикання адресата алертів для
 * одного користувача блокувало перемикачі ВСІХ решти: адміну, що налаштовує
 * список адресатів (`D-125` — це ДАНІ, і їх міняють саме списком), доводилося
 * чекати кожен запит, не розуміючи, чому поля погасли.
 *
 * ⚠ Запит навмисно лишається в дорозі: саме у цьому вікні й видно, чиї поля
 * заблоковані.
 */
const Users = {
  items: [
    {
      id: 1,
      userName: 'first.user',
      displayName: 'First User',
      provider: 'Windows',
      email: 'first@example.com',
      isActive: true,
      isBootstrapAdmin: false,
      isLockedOut: false,
      mustChangePassword: false,
      receivesAlerts: false,
    },
    {
      id: 2,
      userName: 'second.user',
      displayName: 'Second User',
      provider: 'Windows',
      email: 'second@example.com',
      isActive: true,
      isBootstrapAdmin: false,
      isLockedOut: false,
      mustChangePassword: false,
      receivesAlerts: false,
    },
  ],
  nextCursor: null,
  totalCount: 2,
};

let settleAlerts: (() => void) | null = null;

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function stubFetch(): void {
  settleAlerts = null;

  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (init?.method === 'PUT' && url.includes('/alerts')) {
        return new Promise<Response>((resolve) => {
          settleAlerts = () => resolve(jsonResponse(null));
        });
      }

      if (url.includes('/roles')) return Promise.resolve(jsonResponse([]));
      if (url.includes('/users')) return Promise.resolve(jsonResponse(Users));

      if (url.includes('/me')) {
        return Promise.resolve(
          jsonResponse({
            denies: [],
            grants: {},
            isSimulation: false,
            language: 'en',
            mustChangePassword: false,
            permissions: ['Security.ManageUsers'],
            simulatedForUserId: null,
            userId: 9,
            userName: 'tester',
          }),
        );
      }

      return Promise.resolve(jsonResponse([]));
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/security?tab=users']}>
          <SecurityPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Перемикач алертів у рядку названого користувача. */
function alertsSwitchOf(userName: string): HTMLInputElement {
  return screen.getByLabelText(new RegExp(`security\\.alerts.*${userName}`)) as HTMLInputElement;
}

afterEach(() => {
  vi.unstubAllGlobals();
  settleAlerts = null;
});

describe('SecurityPage: перемикач алертів блокується рядком, а не сторінкою (§10.8)', () => {
  it('запит для одного користувача не блокує перемикач іншого', async () => {
    stubFetch();
    show();

    await screen.findByText('first.user');

    fireEvent.click(alertsSwitchOf('first.user'));

    // Свій перемикач справді чекає на відповідь.
    await waitFor(() => expect(alertsSwitchOf('first.user').disabled).toBe(true));

    // ⛔ Мутаційний доказ (RED до фіксу): `alerts.isPending` — одне значення на
    // всю сторінку, тож перемикач другого користувача теж гас, і налаштувати
    // список адресатів можна було лише по одному, з паузою на кожен.
    expect(alertsSwitchOf('second.user').disabled).toBe(false);

    settleAlerts?.();

    await waitFor(() => expect(alertsSwitchOf('first.user').disabled).toBe(false));
  });
});
