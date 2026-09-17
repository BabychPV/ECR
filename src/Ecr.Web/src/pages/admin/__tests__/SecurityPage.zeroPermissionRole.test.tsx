import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { SecurityPage } from '@/pages/admin/SecurityPage';
import { withTestDefaults } from '@/test/render';

/**
 * UI-аудит, lane 1: `lane1-zero-permission-role-no-warning.md` — роль без
 * жодного права (ні звичайного, ні небезпечного) у таблиці ролей виглядала
 * так само, як роль, звужена свідомо: рядок порожній, і нічого не пояснює
 * різницю між «так і задумано» і «нічого не робить».
 *
 * Тест доводить бейдж «no permissions» РІВНО там, де `permissions` і
 * `dangerousPermissions` обидва порожні, і РІВНО НЕ там, де хоча б одне з
 * двох непорожнє — інакше «завжди показувати» чи «завжди ховати» пройшли б
 * так само, як і правильний фікс.
 */

const SeededStrings: Record<string, string> = {
  'security.role': 'Role',
  'security.login': 'Login',
  'security.name': 'Name',
  'security.kind': 'Kind',
  'security.userState': 'State',
  'security.access': 'Access',
  'security.alerts': 'Alerts',
  'security.builtIn': 'built-in',
  'security.inactive': 'inactive',
  'security.dangerous': '{count} dangerous permission(s)',
  'security.noPermissions': 'no permissions',
  'security.mustChangePassword': 'Must change password',
  'security.lockedOut': 'Locked out',
  'security.bootstrap': 'Bootstrap',
  'security.noUsers': 'No users',
  'security.noUsersHint': 'No users hint',
  'security.noRoles': 'No roles',
  'security.noRolesHint': 'No roles hint',
  'security.alertsNeedEmail': 'Needs an email address',
};

const RolesResponse = [
  {
    id: 1,
    code: 'EmptyRole',
    isActive: true,
    isBuiltIn: false,
    dangerousPermissions: [],
    permissions: [],
  },
  {
    id: 2,
    code: 'GrantingRole',
    isActive: true,
    isBuiltIn: false,
    dangerousPermissions: [],
    permissions: ['Document.View'],
  },
  {
    id: 3,
    code: 'DangerousOnlyRole',
    isActive: true,
    isBuiltIn: false,
    dangerousPermissions: ['Security.ManageUsers'],
    permissions: [],
  },
];

function stubFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) {
        return new Response(
          JSON.stringify({ languageCode: 'en', revision: 1, strings: SeededStrings }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      if (url.includes('/roles')) {
        return new Response(JSON.stringify(RolesResponse), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      if (url.includes('/me')) {
        return new Response(
          JSON.stringify({
            userId: 0,
            userName: 'test',
            language: 'en',
            permissions: [],
            isSimulation: false,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      return new Response(JSON.stringify([]), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

beforeEach(() => {
  stubFetch();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function renderRoles() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/security?tab=roles']}>
          <SecurityPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('SecurityPage: попередження про роль без жодного права (lane1)', () => {
  it('роль без permissions і dangerousPermissions отримує бейдж «no permissions»', async () => {
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderRoles();

    const emptyCell = (await screen.findByText('EmptyRole')).closest('td');
    const grantingCell = (await screen.findByText('GrantingRole')).closest('td');
    const dangerousCell = (await screen.findByText('DangerousOnlyRole')).closest('td');
    expect(emptyCell).not.toBeNull();
    expect(grantingCell).not.toBeNull();
    expect(dangerousCell).not.toBeNull();

    // ⛔ Мутаційний доказ: бейдж стоїть РІВНО в клітинці ролі без жодного
    // права.
    expect(within(emptyCell as HTMLElement).getByText('no permissions')).not.toBeNull();

    // ⛔ І РІВНО НЕ стоїть там, де є звичайне право...
    expect(within(grantingCell as HTMLElement).queryByText('no permissions')).toBeNull();

    // ...чи лише небезпечне — воно теж рахується як «щось дає» для мети
    // цього попередження (адмін бачив хоча б один ефект призначення ролі).
    expect(within(dangerousCell as HTMLElement).queryByText('no permissions')).toBeNull();
  });
});
