import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RoleView, UserView } from '@/api/types';
import { UserAccessEditor } from '@/features/security/UserAccessEditor';
import { testTheme } from '@/test/render';

/**
 * ФВ-6.16: розріз ефективного доступу живе в діалозі доступу користувача й підвантажується ЛІНИВО —
 * лише після кнопки, щоб сторінка безпеки не платила за нього в чанку (бюджет `npm run budget`).
 */
const user: UserView = {
  id: 7,
  userName: 'ivanov',
  displayName: 'Іванов',
  email: null,
  provider: 'Local',
  isActive: true,
  isBootstrapAdmin: false,
  isLockedOut: false,
  mustChangePassword: false,
  receivesAlerts: false,
  lastSignInAt: null,
};

const roles: RoleView[] = [];

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('UserAccessEditor: розріз ефективного доступу', () => {
  it('панелі немає, доки не натиснуто кнопку; кнопка показує і ховає її', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        Promise.resolve(
          new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } }),
        ),
      ),
    );

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <UserAccessEditor user={user} roles={roles} onClose={() => {}} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    expect(screen.queryByTestId('effective-access-panel')).toBeNull();

    fireEvent.click(await screen.findByRole('button', { name: /effectiveAccess\.show/ }));
    expect(await screen.findByTestId('effective-access-panel')).toBeDefined();

    fireEvent.click(screen.getByRole('button', { name: /effectiveAccess\.hide/ }));
    expect(screen.queryByTestId('effective-access-panel')).toBeNull();
  });
});
