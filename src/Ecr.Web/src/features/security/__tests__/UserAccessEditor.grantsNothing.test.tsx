import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RoleView, UserView } from '@/api/types';
import { UserAccessEditor } from '@/features/security/UserAccessEditor';
import { testTheme } from '@/test/render';

/**
 * UI-аудит, lane 1: `lane1-zero-permission-role-no-warning.md` —
 * призначений перелік ролей МІГ бути непорожнім і водночас не давати
 * жодного права. `AsyncBoundary`'s «ролей немає» (`Q-254`) цього не бачить
 * узагалі: з погляду мультиселекту роль ПРИЗНАЧЕНА, форма виглядає готовою
 * до збереження, і жодного натяку, чому обліковий запис і далі нічого не
 * бачить, немає.
 *
 * ⚠ `MultiSelect` (`@mantine/core`) під jsdom «зависає» — той самий
 * прийом заміни на стаб-контрол, що й у `UserAccessEditor.clearRoles.
 * test.tsx`.
 */
vi.mock('@mantine/core', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/core')>();

  function StubMultiSelect(props: {
    label?: string;
    value?: string[];
    onChange?: (value: string[]) => void;
  }): JSX.Element {
    return (
      <div>
        <div aria-label={props.label} data-testid="roles-value">
          {(props.value ?? []).join(',')}
        </div>
        <button type="button" onClick={() => props.onChange?.(['EmptyRole'])}>
          select empty-role
        </button>
        <button type="button" onClick={() => props.onChange?.(['GrantingRole'])}>
          select granting-role
        </button>
      </div>
    );
  }

  return { ...actual, MultiSelect: StubMultiSelect };
});

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
};

const roles: RoleView[] = [
  { id: 1, code: 'EmptyRole', isActive: true, isBuiltIn: false, dangerousPermissions: [], permissions: [] },
  {
    id: 2,
    code: 'GrantingRole',
    isActive: true,
    isBuiltIn: false,
    dangerousPermissions: [],
    permissions: ['Document.View'],
  },
];

function respond(body: unknown): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    ),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <UserAccessEditor user={user} roles={roles} onClose={() => {}} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('UserAccessEditor: попередження, коли обрані ролі нічого не дають (lane1)', () => {
  it('обрана роль без жодного права показує попередження «grants nothing»', async () => {
    respond([]);
    show();

    await waitFor(() => {
      expect(screen.getByTestId('roles-value').textContent).toBe('');
    });

    screen.getByText('select empty-role').click();

    // ⛔ Мутаційний доказ: попередження з'являється РІВНО тоді, коли всі
    // обрані ролі порожні на права.
    expect(await screen.findByText('⟦security.rolesGrantNothingTitle⟧')).toBeDefined();
  });

  it('обрана роль, що дає право, попередження НЕ показує', async () => {
    respond([]);
    show();

    await waitFor(() => {
      expect(screen.getByTestId('roles-value').textContent).toBe('');
    });

    screen.getByText('select granting-role').click();

    await waitFor(() => {
      expect(screen.getByTestId('roles-value').textContent).toBe('GrantingRole');
    });

    // ⛔ І РІВНО НЕ з'являється, коли обрана роль щось дає — інакше
    // «показувати завжди» пройшло б так само, як і правильний фікс.
    expect(screen.queryByText('⟦security.rolesGrantNothingTitle⟧')).toBeNull();
  });
});
