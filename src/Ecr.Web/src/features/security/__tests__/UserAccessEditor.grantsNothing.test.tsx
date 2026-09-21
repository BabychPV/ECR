import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
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
 * ✎ Тут `MultiSelect` підмінявся стаб-контролом із двома кнопками — нібито
 * тому, що справжній «зависає під jsdom». Причина зависання знайдена й
 * усунена: взаємна рекурсія jsdom ↔ nwsapi на станових псевдокласах
 * (коментар у `src/test/setup.ts`). Роль обирається у справжньому списку.
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

const RolesLabel = '⟦security.roles⟧';

/** Обрані ролі так, як їх показує `MultiSelect` — «пігулками» над полем. */
function selectedRoles(): string[] {
  return Array.from(document.querySelectorAll('.mantine-Pill-label')).map(
    (pill) => pill.textContent ?? '',
  );
}

/**
 * Обирає роль у справжньому списку.
 *
 * ⚠ Поле береться ДО розкриття списку: щойно список відкрито, підпис «ролі»
 * мають два елементи, і `getByLabelText` стає неоднозначним.
 */
async function pickRole(code: string): Promise<void> {
  const field = await screen.findByLabelText(RolesLabel);
  expect(selectedRoles()).toEqual([]);

  fireEvent.click(field);
  fireEvent.click(screen.getByRole('option', { name: code }));
}

describe('UserAccessEditor: попередження, коли обрані ролі нічого не дають (lane1)', () => {
  it('обрана роль без жодного права показує попередження «grants nothing»', async () => {
    respond([]);
    show();

    await pickRole('EmptyRole');

    // ⛔ Мутаційний доказ: попередження з'являється РІВНО тоді, коли всі
    // обрані ролі порожні на права.
    expect(await screen.findByText('⟦security.rolesGrantNothingTitle⟧')).toBeDefined();
  });

  it('обрана роль, що дає право, попередження НЕ показує', async () => {
    respond([]);
    show();

    await pickRole('GrantingRole');

    await waitFor(() => {
      expect(selectedRoles()).toEqual(['GrantingRole']);
    });

    // ⛔ І РІВНО НЕ з'являється, коли обрана роль щось дає — інакше
    // «показувати завжди» пройшло б так само, як і правильний фікс.
    expect(screen.queryByText('⟦security.rolesGrantNothingTitle⟧')).toBeNull();
  });
});
