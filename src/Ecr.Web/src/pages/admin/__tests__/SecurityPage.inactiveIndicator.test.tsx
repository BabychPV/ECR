import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { SecurityPage } from '@/pages/admin/SecurityPage';

/**
 * UX-аудит `SecurityPage`, знахідка 2/3: неактивні ролі й користувачі
 * сигналізувалися ЛИШЕ `opacity={0.5}` на рядку — жодного тексту, жодного
 * `aria-label`. Непомітно на тьмяному екрані (низький контраст саме там,
 * де мало бути найпомітніше — «це вимкнено»), і зовсім невидимо для читалки:
 * `opacity` не входить у доступне ім'я жодного елемента.
 *
 * Тест рендерить справжню сторінку (обидві вкладки через адресу — так само,
 * як обирає їх сам компонент, `useUrlState`) із мокованим каталогом і по
 * одному АКТИВНОМУ й ОДНОМУ НЕАКТИВНОМУ рядку в кожній таблиці — щоб довести
 * не лише «бейдж десь є», а що він з'являється РІВНО там, де isActive === false,
 * і відсутній там, де isActive === true (інакше «завжди показувати бейдж»
 * пройшов би так само, як і правильний фікс).
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
    code: 'ActiveRole',
    isActive: true,
    isBuiltIn: false,
    dangerousPermissions: [],
    permissions: [],
  },
  {
    id: 2,
    code: 'RetiredRole',
    isActive: false,
    isBuiltIn: false,
    dangerousPermissions: [],
    permissions: [],
  },
];

const UsersResponse = {
  items: [
    {
      id: 1,
      userName: 'active.user',
      displayName: 'Active User',
      provider: 'Windows',
      email: 'active@example.com',
      isActive: true,
      isBootstrapAdmin: false,
      isLockedOut: false,
      mustChangePassword: false,
      receivesAlerts: false,
    },
    {
      id: 2,
      userName: 'retired.user',
      displayName: 'Retired User',
      provider: 'Windows',
      email: null,
      isActive: false,
      isBootstrapAdmin: false,
      isLockedOut: false,
      mustChangePassword: false,
      receivesAlerts: false,
    },
  ],
  nextCursor: null,
  totalCount: 2,
};

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
      if (url.includes('/users')) {
        return new Response(JSON.stringify(UsersResponse), {
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

function renderTab(tab: 'roles' | 'users') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[`/admin/security?tab=${tab}`]}>
          <SecurityPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('SecurityPage: текстовий індикатор неактивності (знахідка 2/3)', () => {
  it('роль isActive=false отримує бейдж «inactive»; активна роль — ні', async () => {
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderTab('roles');

    const retiredCell = (await screen.findByText('RetiredRole')).closest('td');
    const activeCell = (await screen.findByText('ActiveRole')).closest('td');
    expect(retiredCell).not.toBeNull();
    expect(activeCell).not.toBeNull();

    // ⛔ Мутаційний доказ: бейдж має стояти РІВНО в клітинці неактивної ролі.
    expect(within(retiredCell as HTMLElement).getByText('inactive')).not.toBeNull();
    // ⛔ І РІВНО НЕ стояти в клітинці активної — інакше «завжди показувати»
    // теж пройшов би цей тест.
    expect(within(activeCell as HTMLElement).queryByText('inactive')).toBeNull();
  });

  it('користувач isActive=false отримує бейдж «inactive»; активний — ні', async () => {
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderTab('users');

    const retiredCell = (await screen.findByText('retired.user')).closest('td');
    const activeCell = (await screen.findByText('active.user')).closest('td');
    expect(retiredCell).not.toBeNull();
    expect(activeCell).not.toBeNull();

    expect(within(retiredCell as HTMLElement).getByText('inactive')).not.toBeNull();
    expect(within(activeCell as HTMLElement).queryByText('inactive')).toBeNull();
  });
});
