import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RoleView, UserView } from '@/api/types';
import { UserAccessEditor } from '@/features/security/UserAccessEditor';
import { testTheme } from '@/test/render';

/**
 * Ролі наявного користувача: стани «немає права» і «завантаження» (`ФВ-14.22`).
 *
 * ⚠ 5xx і порожній перелік уже стереже `user-access-editor.test.tsx` (`Q-254`).
 * Тут — решта: 403 і запит у дорозі НЕ виглядають як «ролей немає», а
 * «Зберегти» вимкнене — PUT ролей є повною заміною, і порожній `selected`
 * мовчки зняв би всі права.
 */
configure({ asyncUtilTimeout: 10_000 });

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

const roles: RoleView[] = [
  { id: 1, code: 'DataEntry', isActive: true, isBuiltIn: false, dangerousPermissions: [], permissions: [] },
];

const RolesUrl = '/api/v1/users/7/roles';

function serve(rolesAnswer: () => Promise<Response>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.endsWith(RolesUrl)) return rolesAnswer();

      // Області ролей і довідники — порожні: предмет тут лише перелік ролей.
      return new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <UserAccessEditor user={user} roles={roles} onClose={() => {}} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function saveButton(): HTMLButtonElement {
  return screen.getByRole('button', { name: '⟦common.save⟧' }) as HTMLButtonElement;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('UserAccessEditor — стани', () => {
  it('403: «немає права» з кодом, а не «ролей немає»; зберегти не можна', async () => {
    serve(() =>
      Promise.resolve(
        new Response(
          JSON.stringify({ title: 'Forbidden', status: 403, errorCode: 'ECR-AUTH-0403', correlationId: 'c', detail: null }),
          { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    );
    show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-AUTH-0403');
    expect(screen.queryByText('⟦security.noRolesTitle⟧')).toBeNull();
    expect(saveButton().disabled).toBe(true);
  });

  it('у дорозі: «завантаження», а не «ролей немає»; зберегти не можна', async () => {
    serve(() => new Promise<Response>(() => undefined));
    show();

    expect((await screen.findByRole('status')).getAttribute('aria-busy')).toBe('true');
    expect(screen.queryByText('⟦security.noRolesTitle⟧')).toBeNull();
    expect(screen.queryByText('⟦security.noRolesWarning⟧')).toBeNull();
    expect(saveButton().disabled).toBe(true);
  });
});
